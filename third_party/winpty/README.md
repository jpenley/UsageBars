# winpty (vendored)

`winpty.dll` and `winpty-agent.exe` from [winpty](https://github.com/rprichard/winpty) 0.4.3,
as shipped in Git for Windows (`C:\Program Files\Git\usr\bin`).

UsageBars needs a **real pseudo-console** to drive the Claude Code CLI's interactive `/usage`
screen: Claude Code refuses to run interactively unless it believes it has a TTY. A hand-rolled
ConPTY is not accepted (it falls back to `--print` mode and exits), but winpty's pseudo-console
is.

Only these two files are required, and only via winpty's C API (`libwinpty`):

| File | Why |
|---|---|
| `winpty.dll` | the `winpty_*` C API that UsageBars P/Invokes |
| `winpty-agent.exe` | helper the DLL launches to host the hidden console |

The `winpty.exe` **CLI is deliberately not used** — it is an MSYS build and needs
`msys-2.0.dll`, which would not be portable. Both files above are native MSVC builds with no
msys dependency (verified: neither imports `msys-2.0.dll`).

They are embedded into `UsageBars.exe` as resources and extracted to
`%LOCALAPPDATA%\UsageBars\winpty\` on first use, so the published exe stays a single portable
file.

## License

winpty is MIT licensed, Copyright (c) 2011-2016 Ryan Prichard and contributors.
See <https://github.com/rprichard/winpty/blob/master/LICENSE>.
