// ============================================================
//  XDQuestVoice.cs  — конвертация в формат XDQuest (DezLife).
//
//  ФОРМАТ:
//    {"voiceData":"<base64 Ogg Vorbis>","audioType":0,"durationSeconds":<float>}
//    Ogg Vorbis: mono, 48000 Hz, quality ~5 (~128 kbps VBR).
//
//  CHANGE: добавлена поддержка формата XDQuest.
// ============================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NpcSoundConverter
{
    internal static class XDQuestVoice
    {
        /// <summary>
        /// Конвертирует вход в формат XDQuest: Ogg Vorbis mono 48kHz в base64.
        /// </summary>
        public static string Convert(string input, string name, string outDir)
        {
            string ffmpeg = FfmpegProvider.GetFfmpegPath();
            Directory.CreateDirectory(outDir);
            string tmpOgg = Path.Combine(Path.GetTempPath(), "xdq_" + Guid.NewGuid().ToString("N") + ".ogg");
            try
            {
                // 1) конвертируем в Ogg Vorbis: mono, 48 kHz, quality 5
                RunFfmpeg(ffmpeg, "-y -hide_banner -loglevel error -i \"" + input +
                    "\" -vn -ac 1 -ar 48000 -c:a libvorbis -q:a 5 \"" + tmpOgg + "\"");
                if (!File.Exists(tmpOgg) || new FileInfo(tmpOgg).Length == 0)
                    throw new Exception("ffmpeg не смог конвертировать (формат не поддержан?).");

                // 2) длительность (через ffmpeg -i -> Duration)
                double duration = ProbeDuration(ffmpeg, tmpOgg);

                // 3) base64 + JSON {voiceData, audioType, durationSeconds}
                byte[] bytes = File.ReadAllBytes(tmpOgg);
                string b64 = System.Convert.ToBase64String(bytes);
                string durStr = duration.ToString("0.######", CultureInfo.InvariantCulture);
                string json = "{\"voiceData\":\"" + b64 + "\",\"audioType\":0,\"durationSeconds\":" + durStr + "}";

                string outFile = Path.Combine(outDir, name + ".json");
                File.WriteAllText(outFile, json, new UTF8Encoding(false));
                return outFile;
            }
            finally
            {
                try { if (File.Exists(tmpOgg)) File.Delete(tmpOgg); } catch { }
            }
        }

        private static double ProbeDuration(string ffmpeg, string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -i \"" + file + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (m.Success)
                {
                    int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    return h * 3600 + mi * 60 + s;
                }
            }
            return 0.0;
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
}
