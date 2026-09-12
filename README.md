# TimeSync

A tiny Windows tray app that fixes a clock the Windows Time service won't.

![the icon set](icons/contact-sheet.png)

It queries several NTP servers itself, over plain UDP, and sets the system clock
directly. No dependency on `w32time`, no scheduled task, no runtime to install —
a single ~80 KB executable built by a compiler that already ships with Windows.

## Why this exists

A laptop with a dead battery loses its real-time clock every time the charger
comes off. On many designs (this was written for an HP Pavilion Power 15-cb0xx)
the main battery is what keeps the RTC powered, so every cold boot comes up with
the date and time wrong — sometimes by days, sometimes by years.

Windows is supposed to make that a non-event by re-syncing from the network at
boot. Two things stopped that from working:

1. **Something on the machine kept re-disabling `w32time`.** It was configured
   correctly, verified working, and found `Stopped` / `Disabled` with
   `Type=NoSync` again weeks later with nobody having touched it. Likely an
   LTSC/IoT image baseline reasserting itself.
2. **`Max*PhaseCorrection` set to `0xFFFFFFFF` silently refuses every
   correction.** Microsoft documents that value as "always correct the time". In
   practice `w32time` then rejected a 65,035 second correction while logging
   Event 34 claiming it "will not change the system time by more than 4294967295
   seconds" — a limit far *above* what it was refusing. It behaves as though the
   value is compared as a signed `-1`, making every correction look too large.
   `0x7FFFFFFF` works; `0xFFFFFFFF` does not.

Rather than keep fighting the time service, this bypasses it entirely.

## How it works

**Plain SNTP over UDP/123, deliberately.** NTP performs no certificate
validation, so it works with the clock arbitrarily wrong. An HTTPS time API
would fail TLS validation in precisely the situation the tool exists to fix —
a certificate is not yet valid if your clock thinks it is 2019.

**Safety by agreement, not by magnitude.** There is no cap on how large a
correction may be, because an RTC that lost power is legitimately years out and
a cap is exactly what breaks the fix when it is needed most. Instead, four
servers are queried and the clock is only set when at least two agree within
five seconds of the median. A single unreachable, broken or hostile server
cannot move your clock; a genuine seven-year correction goes through unimpeded.

**Every measurement is anchored to a monotonic `Stopwatch`.** The wall clock is
the thing being corrected — it jumps mid-run — so wall-clock arithmetic would be
self-corrupting. Samples taken seconds apart are normalised to a common
monotonic origin before they are compared.

**`SeSystemtimePrivilege` is enabled explicitly.** Administrators hold it, but it
is *disabled* in the token by default and `SetSystemTime` fails with error 1314
without an `AdjustTokenPrivileges` call first.

At startup it syncs immediately, retries every 10 seconds while it fails (the
network is rarely up yet at boot), then settles into an hourly re-check.

### Verified in the wild

On a real cold boot with a drifted RTC it applied a **−161,990 second** (−45
hour) correction. Checked afterwards with an independent client:

```
w32tm /stripchart /computer:time.cloudflare.com /samples:2 /dataonly
  +00.0020418s
  +00.0024525s
```

Two milliseconds off, from 45 hours wrong.

## Install

1. Build it (see below) or drop `TimeSync.exe` anywhere you like.
2. Press <kbd>Win</kbd>+<kbd>R</kbd>, run `shell:startup`, and put a shortcut to
   `TimeSync.exe` in that folder.

That is all. In particular, do **not** tick "Run as administrator" on that
shortcut: Windows silently skips Startup-folder items that request elevation —
it does not prompt, it simply never launches them — so the app would appear to
do nothing at every login. It elevates itself instead.

### Elevation

Setting the system clock requires `SeSystemtimePrivilege`, and a UAC-filtered
token does not merely have it disabled — it is absent, even when the account is
an administrator. So the shortcut starts the app unelevated and the app
immediately relaunches itself through `ShellExecute` with the `runas` verb, and
the unelevated instance exits.

Depending on your UAC setting that is either one prompt at login, or entirely
silent (where *Elevate without prompting* is configured). If elevation is
refused the app keeps running anyway: the tray icon turns red, the window says
why, and **Restart as admin** retries on demand.

The relaunched child is marked `--elevated` so that an elevation which somehow
succeeds without granting admin cannot spawn an endless chain of relaunches.

## Using it

Left-click the tray icon for a window showing live state: elevation, system
clock versus true time, the current offset, how many servers agreed, last
success, attempt count and a running log. Buttons for **Sync now**,
**Restart as admin**, **Open log**, **Hide** and **Quit**.

The tray icon colour tracks state — 🟢 synced, 🔵 syncing, 🟠 retrying,
🔴 no permission — and everything is also appended to `timesync.log` beside the
executable.

| Argument | Effect |
|---|---|
| *(none)* | Run the tray app |
| `--show` | Run the tray app with the debug window already open |
| `--check` | Headless one-shot; writes the measured offset to the log and exits |
| `--export-icons` | Regenerate the icon set into `icons/` |

## Building

```
build.cmd
```

That is the whole toolchain. It uses `csc.exe` from
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`, which is present on any machine
with .NET Framework 4.x — no SDK, no NuGet, no package restore.

One constraint worth knowing before you edit the source: that compiler only
supports **C# 5**. No string interpolation, no `?.`, no expression-bodied
members. The source sticks to that deliberately.

## The icons

The four state icons are compiled into the executable as `.ico` resources and
loaded once at startup. Only the tray sizes — 16/24/32/48, about 23 KB total —
are embedded: a tray icon is never drawn larger than 48px, so carrying the 128
and 256 entries would have cost roughly five times that for pixels nothing ever
reads. The exe's own shell icon does use the full set, since Explorer wants
those larger sizes in its big-icon views.

`RabbitArt` is still the source of truth for the artwork. It draws the design
from GDI+ primitives, and `--export-icons` regenerates everything from it:
`icons/*.ico` as multi-resolution files (16 → 256, PNG-compressed entries),
`icons/tray/*.ico` at the embedding sizes, PNGs at 16/32/64/256, and a contact
sheet. Change the drawing code and you must re-run `--export-icons` and rebuild,
or the exe keeps serving the old embedded copies.

The watch face carries the state colour because it is the largest solid block in
the design and is the only element that still reads at 16×16 in the tray.

The artwork is original. The White Rabbit and his pocket watch come from Lewis
Carroll (1865) and are public domain; the drawing is not traced or derived from
any existing illustration, so the set is genuinely free to reuse.

## Limitations

**It may prompt for UAC at every login.** The app has to elevate itself to set
the clock (see [Elevation](#elevation)). Under default UAC settings that means
one consent prompt per login. If that bothers you, the alternative is a
scheduled task with *Run with highest privileges* triggered at logon, which
bypasses UAC entirely at the cost of needing to be installed.

**It only fixes the clock.** UEFI settings live in SPI-flash NVRAM and are not
battery-backed, so boot order and Secure Boot survive an RTC loss. If those
start resetting too, no software can help and the battery needs replacing.

**Single machine, single purpose.** The server list is hardcoded and one entry
is Japan-specific. Edit `Ntp.Servers` in `TimeSync.cs`.

## License

[The Unlicense](UNLICENSE) — public domain, code and icons alike.
