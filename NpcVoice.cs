// ============================================================
//  NpcVoice.cs  — формат HumanNPC voice (Steam Voice, Hybrid SWB Opus).
//
//  Кодек: НАТИВНЫЙ libopus (opus.dll), режим Hybrid SuperWideBand (cfg 13),
//  20 мс одиночные фреймы (code 0) — байт-в-байт как у in-game записей,
//  гладко на речи (без CELT pre-echo и без SILK-щелчков на стыках).
//
//  Контейнер:
//    {"Data": base64( GZip( [int32 LE len]xN ++ chunkBytes ) )}
//    chunk = [SteamID64 LE][0x0B + u16 24000][0x06 + u16 len + opusPLC][CRC32 IEEE]
//    opusPLC = повтор [u16 frameLen][u16 seq][opus 20мс]
//
//  CHANGE: кодек переключён с Concentus(CELT/SILK) на нативный libopus Hybrid SWB.
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NpcSoundConverter
{
    internal static class NpcVoice
    {
        private const int SampleRate = 24000;       // оптимальная частота Steam Voice
        private const int FrameSamples = 480;        // 20 мс @ 24 кГц -> одиночный opus-фрейм (code 0)
        private const int FramesPerChunk = 5;        // ~100 мс аудио на один голосовой пакет
        private const int Bitrate = 32000;           // как в рабочих файлах
        private const ulong SteamId = 76561198448024057UL;

        public static string Convert(string input, string name, string outDir)
        {
            string ffmpeg = FfmpegProvider.GetFfmpegPath();
            OpusNative.EnsureLoaded();
            Directory.CreateDirectory(outDir);
            string tmpPcm = Path.Combine(Path.GetTempPath(), "npcsnd_" + Guid.NewGuid().ToString("N") + ".pcm");
            try
            {
                RunFfmpeg(ffmpeg, "-y -hide_banner -loglevel error -i \"" + input +
                    "\" -vn -ac 1 -ar 24000 -f s16le -acodec pcm_s16le \"" + tmpPcm + "\"");
                if (!File.Exists(tmpPcm) || new FileInfo(tmpPcm).Length == 0)
                    throw new Exception("ffmpeg не смог декодировать аудио (формат не поддержан?).");

                short[] pcm = ReadPcm16(tmpPcm);
                if (pcm.Length == 0) throw new Exception("Пустое аудио.");

                List<byte[]> chunks = EncodeChunks(pcm);
                if (chunks.Count == 0) throw new Exception("Не удалось закодировать аудио.");

                byte[] saveData = ToSaveData(chunks);
                byte[] gz = GzipCompress(saveData);
                string json = "{\"Data\":\"" + System.Convert.ToBase64String(gz) + "\"}";

                string outFile = Path.Combine(outDir, name + ".json");
                File.WriteAllText(outFile, json, new UTF8Encoding(false));
                return outFile;
            }
            finally { try { if (File.Exists(tmpPcm)) File.Delete(tmpPcm); } catch { } }
        }

        private static short[] ReadPcm16(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            int n = raw.Length / 2;
            short[] s = new short[n];
            Buffer.BlockCopy(raw, 0, s, 0, n * 2);
            return s;
        }

        // Кодируем 20 мс фреймы нативным libopus в режиме Hybrid SWB, группируем по чанкам.
        private static List<byte[]> EncodeChunks(short[] pcm)
        {
            IntPtr enc = OpusNative.CreateHybridSwbEncoder(SampleRate, Bitrate);
            try
            {
                var frames = new List<byte[]>();
                short[] frame = new short[FrameSamples];
                byte[] opusBuf = new byte[4000];
                for (int pos = 0; pos < pcm.Length; pos += FrameSamples)
                {
                    Array.Clear(frame, 0, frame.Length);
                    int cnt = Math.Min(FrameSamples, pcm.Length - pos);
                    if (cnt > 0) Array.Copy(pcm, pos, frame, 0, cnt);
                    int n = OpusNative.Encode(enc, frame, FrameSamples, opusBuf);
                    if (n <= 0) throw new Exception("opus_encode err=" + n);
                    byte[] f = new byte[n];
                    Buffer.BlockCopy(opusBuf, 0, f, 0, n);
                    frames.Add(f);
                }

                // Структура как у in-game записей (доказано рабочей): первый чанк —
                // одна группа из 8 фреймов; далее чанки из ДВУХ групп SR+OPUS по 3 фрейма;
                // reset в конце последней группы + финальный чанк-тишина.
                var chunks = new List<byte[]>();
                ushort seq = 0;
                int idx = 0;
                bool firstChunk = true;
                while (idx < frames.Count)
                {
                    int[] groupSizes = firstChunk ? new[] { 8 } : new[] { 3, 3 };
                    firstChunk = false;
                    // сколько фреймов реально уйдёт в этот чанк
                    int willUse = 0; foreach (int g in groupSizes) willUse += Math.Min(g, Math.Max(0, frames.Count - idx - willUse));
                    bool isLast = (idx + willUse >= frames.Count);
                    chunks.Add(BuildGroupedPacket(frames, ref idx, ref seq, groupSizes, isLast));
                }
                // финальный чанк: хвостовая тишина (~62 мс), как у soundname
                chunks.Add(BuildSilenceChunk(1500));
                return chunks;
            }
            finally { OpusNative.DestroyEncoder(enc); }
        }

        // Финальный пакет с одним payload'ом тишины (type 0) — чистое завершение потока.
        private static byte[] BuildSilenceChunk(ushort silentSamples)
        {
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ms.Write(BitConverter.GetBytes(SteamId), 0, 8);
                ms.WriteByte(0x0B);
                ms.Write(BitConverter.GetBytes((ushort)SampleRate), 0, 2);
                ms.WriteByte(0x00); // payload type 0 = silence
                ms.Write(BitConverter.GetBytes(silentSamples), 0, 2);
                body = ms.ToArray();
            }
            uint crc = Crc32(body, body.Length);
            byte[] packet = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, packet, 0, body.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, packet, body.Length, 4);
            return packet;
        }

        // Пакет из нескольких групп (каждая = [0x0B SR][0x06 OPUS-фреймы]),
        // как у in-game записей. reset (0xFFFF) добавляется в конец последней группы.
        private static byte[] BuildGroupedPacket(List<byte[]> frames, ref int idx, ref ushort seq, int[] groupSizes, bool appendReset)
        {
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ms.Write(BitConverter.GetBytes(SteamId), 0, 8);
                for (int gi = 0; gi < groupSizes.Length; gi++)
                {
                    int take = Math.Min(groupSizes[gi], frames.Count - idx);
                    if (take <= 0) break;
                    byte[] inner;
                    using (var ims = new MemoryStream())
                    {
                        for (int k = 0; k < take; k++)
                        {
                            byte[] f = frames[idx++];
                            ims.Write(BitConverter.GetBytes((ushort)f.Length), 0, 2);
                            ims.Write(BitConverter.GetBytes(seq), 0, 2);
                            ims.Write(f, 0, f.Length);
                            seq++;
                        }
                        // reset в конце самой последней группы потока
                        if (appendReset && (gi == groupSizes.Length - 1 || idx >= frames.Count))
                            ims.Write(BitConverter.GetBytes((ushort)0xFFFF), 0, 2);
                        inner = ims.ToArray();
                    }
                    ms.WriteByte(0x0B);
                    ms.Write(BitConverter.GetBytes((ushort)SampleRate), 0, 2);
                    ms.WriteByte(0x06);
                    ms.Write(BitConverter.GetBytes((ushort)inner.Length), 0, 2);
                    ms.Write(inner, 0, inner.Length);
                    if (idx >= frames.Count) break;
                }
                body = ms.ToArray();
            }
            uint crc = Crc32(body, body.Length);
            byte[] packet = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, packet, 0, body.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, packet, body.Length, 4);
            return packet;
        }

        private static byte[] ToSaveData(List<byte[]> data)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var cd in data) ms.Write(BitConverter.GetBytes(cd.Length), 0, 4);
                foreach (var cd in data) ms.Write(cd, 0, cd.Length);
                return ms.ToArray();
            }
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }
        private static uint Crc32(byte[] data, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void RunFfmpeg(string ffmpeg, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception("ffmpeg код " + p.ExitCode + (string.IsNullOrEmpty(err) ? "" : ": " + err.Trim()));
            }
        }
    }

    // Нативный libopus (opus.dll). Режим Hybrid SuperWideBand для речи (cfg 13).
    internal static class OpusNative
    {
        private const string LIB = "opus.dll";

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);
        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int opus_encode(IntPtr st, short[] pcm, int frame_size, byte[] data, int max_data_bytes);
        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int opus_encoder_ctl(IntPtr st, int request, int value);
        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern void opus_encoder_destroy(IntPtr st);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private const int OPUS_APPLICATION_VOIP = 2048;
        private const int SET_BITRATE = 4002, SET_VBR = 4006, SET_BANDWIDTH = 4008,
                          SET_MAX_BANDWIDTH = 4004, SET_COMPLEXITY = 4010, SET_SIGNAL = 4024,
                          SET_INBAND_FEC = 4012, SET_PACKET_LOSS_PERC = 4014, SET_DTX = 4016;
        private const int BW_SUPERWIDEBAND = 1104;
        private const int SIGNAL_VOICE = 3001;

        private static bool _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            // рядом с exe?
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opus.dll");
            string target = local;
            if (!File.Exists(local))
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var res = asm.GetManifestResourceStream("opus.dll"))
                {
                    if (res == null) throw new Exception("opus.dll не вшит в exe и не найден рядом.");
                    string dir = Path.Combine(Path.GetTempPath(), "npc_sound_opus");
                    Directory.CreateDirectory(dir);
                    target = Path.Combine(dir, "opus.dll");
                    if (!File.Exists(target) || new FileInfo(target).Length != res.Length)
                        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write)) res.CopyTo(fs);
                }
            }
            if (LoadLibrary(target) == IntPtr.Zero)
                throw new Exception("Не удалось загрузить opus.dll (нужна x64-версия).");
            _loaded = true;
        }

        public static IntPtr CreateHybridSwbEncoder(int sampleRate, int bitrate)
        {
            int err;
            IntPtr enc = opus_encoder_create(sampleRate, 1, OPUS_APPLICATION_VOIP, out err);
            if (err != 0 || enc == IntPtr.Zero) throw new Exception("opus_encoder_create err=" + err);
            opus_encoder_ctl(enc, SET_BITRATE, bitrate);
            opus_encoder_ctl(enc, SET_VBR, 1);
            opus_encoder_ctl(enc, SET_MAX_BANDWIDTH, BW_SUPERWIDEBAND); // Hybrid SWB (cfg 13)
            opus_encoder_ctl(enc, SET_BANDWIDTH, BW_SUPERWIDEBAND);
            opus_encoder_ctl(enc, SET_SIGNAL, SIGNAL_VOICE);
            opus_encoder_ctl(enc, SET_COMPLEXITY, 10);
            opus_encoder_ctl(enc, SET_DTX, 1); // DTX: паузы кодируются 1-байтовыми фреймами (как у Steam-записей)
            return enc;
        }

        public static int Encode(IntPtr enc, short[] pcm, int frameSize, byte[] outBuf)
        {
            return opus_encode(enc, pcm, frameSize, outBuf, outBuf.Length);
        }

        public static void DestroyEncoder(IntPtr enc)
        {
            if (enc != IntPtr.Zero) opus_encoder_destroy(enc);
        }
    }

    internal static class FfmpegProvider
    {
        private const string ResourceName = "ffmpeg.exe";
        public static string GetFfmpegPath()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            var asm = Assembly.GetExecutingAssembly();
            using (var res = asm.GetManifestResourceStream(ResourceName))
            {
                if (res == null) throw new Exception("ffmpeg не вшит в exe и не найден рядом.");
                string dir = Path.Combine(Path.GetTempPath(), "npc_sound_ffmpeg");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(target) && new FileInfo(target).Length == res.Length) return target;
                using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write)) res.CopyTo(fs);
                return target;
            }
        }
    }
}
