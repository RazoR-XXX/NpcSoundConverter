// ============================================================
//  NpcSoundConverter.cs  — GUI (RU/EN) для конвертации аудио в озвучку NPC (Rust).
//  Форматы: HumanNPC (Steam Voice / Opus) и XDQuest (Ogg Vorbis).
//  Автор: RazoR, 2026. ffmpeg.exe и opus.dll вшиты в exe.
//
//  CHANGE: добавлен переключатель языка RU/EN.
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NpcSoundConverter
{
    internal enum Lang { RU, EN }

    internal static class L
    {
        public static Lang Cur = Lang.RU;
        private static readonly Dictionary<string, string[]> S = new Dictionary<string, string[]>
        {
            // key                {  RU,  EN }
            { "title",   new[]{ "NPC Sound Converter — аудио в озвучку NPC (Rust) — by RazoR, 2026",
                                 "NPC Sound Converter — audio to NPC voice (Rust) — by RazoR, 2026" } },
            { "lblIn",   new[]{ "Аудиофайл (mp3/wav/flac/m4a/aac/opus/ogg...):",
                                 "Audio file (mp3/wav/flac/m4a/aac/opus/ogg...):" } },
            { "browse",  new[]{ "Выбрать...", "Browse..." } },
            { "lblName", new[]{ "Имя озвучки:", "Sound name:" } },
            { "lblFmt",  new[]{ "Формат плагина:", "Plugin format:" } },
            { "lblOut",  new[]{ "Папка сохранения (.json):", "Output folder (.json):" } },
            { "folder",  new[]{ "Папка...", "Folder..." } },
            { "convert", new[]{ "Конвертировать", "Convert" } },
            { "lblLang", new[]{ "Язык:", "Language:" } },
            { "discord", new[]{ "Автор: RazoR (Discord)", "Author: RazoR (Discord)" } },
            { "hintIdle",new[]{ "Перетащи аудиофайл в окно или нажми «Выбрать...».",
                                 "Drag an audio file here or click \"Browse...\"." } },
            { "picked",  new[]{ "Файл выбран: ", "File selected: " } },
            { "notfound",new[]{ "Ошибка: файл не найден.", "Error: file not found." } },
            { "working", new[]{ "Конвертация...", "Converting..." } },
            { "doneTtl", new[]{ "Готово! Сохранено:", "Done! Saved:" } },
            { "err",     new[]{ "Ошибка: ", "Error: " } },
            { "openTtl", new[]{ "Выбери аудиофайл", "Select an audio file" } },
            { "filtAud", new[]{ "Аудио", "Audio" } },
            { "filtAll", new[]{ "Все файлы", "All files" } },
            { "outDesc", new[]{ "Куда сохранить .json озвучки", "Where to save the .json voice file" } },
            { "hintNpc", new[]{ "В игре: /npc_edit -> /npc sound {0} -> /npc soundonuse true -> /npc_end",
                                 "In game: /npc_edit -> /npc sound {0} -> /npc soundonuse true -> /npc_end" } },
            { "hintXdq", new[]{ "В конфиге XDQuest укажи имя озвучки: {0}",
                                 "Set this voice name in your XDQuest config: {0}" } },
        };
        public static string T(string k) { return S[k][(int)Cur]; }
    }

    public sealed class MainForm : Form
    {
        private readonly TextBox _inputBox, _nameBox, _outBox;
        private readonly ComboBox _formatBox, _langBox;
        private readonly Button _convertBtn, _browseIn, _browseOut;
        private readonly Label _lblIn, _lblName, _lblFmt, _lblOut, _lblLang, _status;
        private readonly LinkLabel _discord;
        private const string DiscordUrl = "https://discordapp.com/users/1056019567589216336";

        public MainForm()
        {
            Size = new Size(640, 430);
            MinimumSize = new Size(560, 390);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            Font = new Font("Segoe UI", 9f);

            _lblLang = new Label { Left = 14, Top = 12, AutoSize = true };
            _langBox = new ComboBox { Left = 110, Top = 9, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            _langBox.Items.AddRange(new object[] { "Русский", "English" });
            _langBox.SelectedIndex = 0;
            _langBox.SelectedIndexChanged += (s, e) => { L.Cur = (Lang)_langBox.SelectedIndex; ApplyLang(); };

            _discord = new LinkLabel { Top = 12, Left = 400, Width = 200, AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _discord.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordUrl) { UseShellExecute = true }); } catch { }
            };

            _lblIn = new Label { Left = 14, Top = 44, Width = 600, AutoSize = true };
            _inputBox = new TextBox { Left = 16, Top = 66, Width = 480, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _browseIn = new Button { Left = 504, Top = 64, Width = 96, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _browseIn.Click += (s, e) => PickInput();

            _lblName = new Label { Left = 14, Top = 104, Width = 600, AutoSize = true };
            _nameBox = new TextBox { Left = 16, Top = 126, Width = 584, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

            _lblFmt = new Label { Left = 14, Top = 164, AutoSize = true };
            _formatBox = new ComboBox { Left = 150, Top = 161, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            _formatBox.Items.AddRange(new object[] { "HumanNPC (Razor)", "XDQuest (DezLife)" });
            _formatBox.SelectedIndex = 0;
            _formatBox.SelectedIndexChanged += (s, e) => _outBox.Text = GuessSoundsDir(_formatBox.SelectedIndex == 1 ? "XDQuest" : "HumanNPC");

            _lblOut = new Label { Left = 14, Top = 200, Width = 600, AutoSize = true };
            _outBox = new TextBox { Left = 16, Top = 222, Width = 480, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _browseOut = new Button { Left = 504, Top = 220, Width = 96, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _browseOut.Click += (s, e) => PickOutput();

            _convertBtn = new Button { Left = 16, Top = 262, Width = 584, Height = 40, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _convertBtn.Click += (s, e) => StartConvert();

            _status = new Label { Left = 16, Top = 312, Width = 584, Height = 80, AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };

            Controls.AddRange(new Control[] { _lblLang, _langBox, _discord, _lblIn, _inputBox, _browseIn, _lblName, _nameBox,
                _lblFmt, _formatBox, _lblOut, _outBox, _browseOut, _convertBtn, _status });

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f != null && f.Length > 0) SetInput(f[0]); };

            _outBox.Text = GuessSoundsDir("HumanNPC");
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch { }
            ApplyLang();
        }

        private void ApplyLang()
        {
            Text = L.T("title");
            _lblLang.Text = L.T("lblLang");
            _lblIn.Text = L.T("lblIn");
            _browseIn.Text = L.T("browse");
            _lblName.Text = L.T("lblName");
            _lblFmt.Text = L.T("lblFmt");
            _lblOut.Text = L.T("lblOut");
            _browseOut.Text = L.T("folder");
            _convertBtn.Text = L.T("convert");
            _discord.Text = L.T("discord");
            _discord.Left = ClientSize.Width - _discord.Width - 16;
            if (string.IsNullOrEmpty(_inputBox.Text)) _status.Text = L.T("hintIdle");
        }

        private void SetInput(string path)
        {
            _inputBox.Text = path;
            if (string.IsNullOrWhiteSpace(_nameBox.Text)) _nameBox.Text = Path.GetFileNameWithoutExtension(path);
            SetStatus(L.T("picked") + Path.GetFileName(path), Color.Black);
        }

        private void PickInput()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = L.T("openTtl");
                d.Filter = L.T("filtAud") + "|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.opus;*.ogg;*.wma;*.mp4;*.webm|" + L.T("filtAll") + "|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) SetInput(d.FileName);
            }
        }

        private void PickOutput()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = L.T("outDesc");
                if (Directory.Exists(_outBox.Text)) d.SelectedPath = _outBox.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _outBox.Text = d.SelectedPath;
            }
        }

        private static string GuessSoundsDir(string plugin)
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null)
                {
                    foreach (var root in new[] { "rustds\\carbon", "carbon", "oxide" })
                    {
                        var cand = Path.Combine(dir.FullName, root, "data", plugin, "Sounds");
                        if (Directory.Exists(cand)) return cand;
                    }
                    dir = dir.Parent;
                }
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
            _status.ForeColor = color; _status.Text = text;
        }

        private void StartConvert()
        {
            string input = _inputBox.Text.Trim().Trim('"');
            string name = _nameBox.Text.Trim();
            string outDir = _outBox.Text.Trim();
            bool isXdq = _formatBox.SelectedIndex == 1;

            if (!File.Exists(input)) { SetStatus(L.T("notfound"), Color.Red); return; }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(input);
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(outDir)) outDir = AppDomain.CurrentDomain.BaseDirectory;

            _convertBtn.Enabled = false;
            SetStatus(L.T("working"), Color.Black);

            var th = new Thread(() =>
            {
                try
                {
                    string outFile = isXdq ? XDQuestVoice.Convert(input, name, outDir) : NpcVoice.Convert(input, name, outDir);
                    string hint = string.Format(L.T(isXdq ? "hintXdq" : "hintNpc"), name);
                    SetStatus(L.T("doneTtl") + "\n" + outFile + "\n\n" + hint, Color.Green);
                }
                catch (Exception ex) { SetStatus(L.T("err") + ex.Message, Color.Red); }
                finally { BeginInvoke(new Action(() => _convertBtn.Enabled = true)); }
            });
            th.IsBackground = true;
            th.Start();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static void Main(string[] args)
        {
            // CLI: NpcSoundConverter.exe [--xdquest] <input> [name] [outDir]
            if (args != null && args.Length >= 1)
            {
                AttachConsole(-1);
                try
                {
                    bool xdq = false; int i = 0;
                    if (args[0] == "--xdquest" || args[0] == "-x") { xdq = true; i = 1; }
                    if (i >= args.Length) { Console.WriteLine("usage: NpcSoundConverter [--xdquest] <input> [name] [outDir]"); Environment.Exit(2); }
                    string input = args[i];
                    string name = args.Length > i + 1 ? args[i + 1] : Path.GetFileNameWithoutExtension(input);
                    string outDir = args.Length > i + 2 ? args[i + 2] : Path.GetDirectoryName(Path.GetFullPath(input));
                    string outFile = xdq ? XDQuestVoice.Convert(input, name, outDir) : NpcVoice.Convert(input, name, outDir);
                    Console.WriteLine("OK: " + outFile); Environment.Exit(0);
                }
                catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.Exit(1); }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
