# NPC Sound Converter

Convert any popular audio file (mp3 / wav / flac / m4a / aac / opus / ogg / wma ...) into a
ready-to-use **NPC voice file** for Rust/Oxide/Carbon plugins:

- **HumanNPC** (by *Razor*) — Steam Voice / Opus format
- **XDQuest** (by *DezLife*) — Ogg Vorbis format

> Author: **RazoR**, 2026 · [Русская версия ниже](#русская-версия) · Full format spec: [NPC_Sound_Formats.md](NPC_Sound_Formats.md)

This is, as far as we know, the **first public file-based converter and format
specification** for HumanNPC voice. Every guide online only says *"record it in-game with a
microphone"* — this tool builds the file directly from any audio.

---

## Features

- One self-contained `.exe` — **ffmpeg and libopus are embedded**, nothing to install.
- Simple GUI: pick a file (button or **drag-and-drop**), choose the output folder, convert.
- **RU / EN** language switch.
- Two output formats: **HumanNPC** and **XDQuest** (switchable).
- CLI mode for scripting/batch.

## Usage (GUI)

1. Run `NpcSoundConverter.exe`.
2. Pick an audio file (button or drag it onto the window).
3. Enter a sound name.
4. Choose the **plugin format** (HumanNPC or XDQuest) — the output folder auto-fills.
5. Click **Convert**.

In game (HumanNPC):
```
o.reload HumanNPC          (or carbon.reload HumanNPC)
/npc_edit  ->  /npc sound <name>  ->  /npc soundonuse true  ->  /npc_end
```
For XDQuest: put the produced `.json` into `data/XDQuest/Sounds/` and reference its name in the config.

## Usage (CLI)

```
NpcSoundConverter.exe [--xdquest] <input-audio> [name] [outputDir]
```
- Default format is HumanNPC; add `--xdquest` for XDQuest.
- `name` defaults to the input filename; `outputDir` defaults to the input's folder.

## Requirements

- Windows 10/11 (x64). .NET Framework 4.x is preinstalled on Windows.
- Nothing else — ffmpeg and libopus are bundled inside the exe.

## Building from source

```
csc /platform:x64 /target:winexe /out:NpcSoundConverter.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.dll ^
  /resource:"<path>\ffmpeg.exe,ffmpeg.exe" /resource:"opus.dll,opus.dll" ^
  NpcSoundConverter.cs NpcVoice.cs XDQuestVoice.cs
```
- `csc.exe` ships with the .NET Framework (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`).
- `opus.dll` (x64 libopus 1.3.x) and any `ffmpeg.exe` are embedded as resources.

## How it works (short version)

- **XDQuest**: a plain **Ogg Vorbis** file (mono, 48 kHz), base64-encoded in
  `{"voiceData":"...","audioType":0,"durationSeconds":...}`.
- **HumanNPC**: a stream of **Steam Voice** packets (the exact bytes the Rust client makes
  with `SteamUser.GetVoice`), GZip-compressed in `{"Data":"..."}`. Each packet =
  `[SteamID64][sample rate 24000][Opus PLC data][CRC32]`; the Opus is **Hybrid SuperWideBand,
  20 ms frames** wrapped as `[u16 len][u16 seq]`, and the packetization mirrors the in-game
  recorder (big first packet, then 2 small groups per packet, reset + trailing silence).

Full byte-level details: **[NPC_Sound_Formats.md](NPC_Sound_Formats.md)** (EN) /
**[NPC_Sound_Formats.ru.md](NPC_Sound_Formats.ru.md)** (RU).

## Credits

- **RazoR** (2026) — converter and format reverse-engineering. Discord: <https://discordapp.com/users/1056019567589216336>
- Steam Voice format cross-checked with *demostf/steam-audio-codec* and the
  *"Reversing Steam Voice Codec"* blog by *Zhenyang Li*.
- [FFmpeg](https://ffmpeg.org/) — audio decoding · [libopus](https://opus-codec.org/) — Opus encoding.

---

# Русская версия

Конвертер любого популярного аудио (mp3 / wav / flac / m4a / aac / opus / ogg / wma ...) в
готовый файл **озвучки NPC** для плагинов Rust/Oxide/Carbon:

- **HumanNPC** (*Razor*) — формат Steam Voice / Opus
- **XDQuest** (*DezLife*) — формат Ogg Vorbis

> Автор: **RazoR**, 2026 · Полная спецификация формата: [NPC_Sound_Formats.ru.md](NPC_Sound_Formats.ru.md)

Насколько известно, это **первый публичный файловый конвертер и спецификация формата**
озвучки HumanNPC. Везде в интернете советуют только «записать в игре через микрофон» — а этот
инструмент собирает файл напрямую из любого аудио.

## Возможности

- Один самодостаточный `.exe` — **ffmpeg и libopus вшиты внутрь**, ничего ставить не надо.
- Простой интерфейс: выбор файла (кнопкой или **перетаскиванием**), папка сохранения, конвертация.
- Переключатель языка **RU / EN**.
- Два формата вывода: **HumanNPC** и **XDQuest** (переключаются).
- CLI-режим для скриптов/пакетной обработки.

## Использование (окно)

1. Запусти `NpcSoundConverter.exe`.
2. Выбери аудиофайл (кнопкой или перетащи в окно).
3. Введи имя озвучки.
4. Выбери **формат плагина** (HumanNPC или XDQuest) — папка сохранения подставится сама.
5. Нажми **Конвертировать**.

В игре (HumanNPC):
```
o.reload HumanNPC          (или carbon.reload HumanNPC)
/npc_edit  ->  /npc sound <имя>  ->  /npc soundonuse true  ->  /npc_end
```
Для XDQuest: положи готовый `.json` в `data/XDQuest/Sounds/` и укажи имя озвучки в конфиге.

## Использование (CLI)

```
NpcSoundConverter.exe [--xdquest] <аудиофайл> [имя] [папка]
```
- По умолчанию формат HumanNPC; для XDQuest добавь `--xdquest`.
- `имя` по умолчанию = имя входного файла; `папка` по умолчанию = папка входного файла.

## Требования

- Windows 10/11 (x64). .NET Framework 4.x уже есть в Windows.
- Больше ничего — ffmpeg и libopus встроены в exe.

## Сборка из исходников

```
csc /platform:x64 /target:winexe /out:NpcSoundConverter.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.dll ^
  /resource:"<путь>\ffmpeg.exe,ffmpeg.exe" /resource:"opus.dll,opus.dll" ^
  NpcSoundConverter.cs NpcVoice.cs XDQuestVoice.cs
```

## Как это устроено (кратко)

- **XDQuest**: обычный **Ogg Vorbis** (моно, 48 кГц) в base64:
  `{"voiceData":"...","audioType":0,"durationSeconds":...}`.
- **HumanNPC**: поток пакетов **Steam Voice** (те же байты, что делает клиент Rust через
  `SteamUser.GetVoice`), сжатый GZip в `{"Data":"..."}`. Каждый пакет =
  `[SteamID64][частота 24000][Opus PLC данные][CRC32]`; opus — **Hybrid SuperWideBand,
  фреймы 20 мс**, обёрнутые в `[u16 len][u16 seq]`, а упаковка повторяет игровую запись
  (большой первый пакет, далее по 2 мелкие группы на пакет, сброс + хвостовая тишина).

Полные детали по байтам: **[NPC_Sound_Formats.ru.md](NPC_Sound_Formats.ru.md)**.

## Благодарности

- **RazoR** (2026) — конвертер и реверс-инжиниринг формата. Discord: <https://discordapp.com/users/1056019567589216336>
- Формат Steam Voice сверен с *demostf/steam-audio-codec* и блогом
  *«Reversing Steam Voice Codec»* (*Zhenyang Li*).
- [FFmpeg](https://ffmpeg.org/) — декодирование аудио · [libopus](https://opus-codec.org/) — кодирование Opus.
