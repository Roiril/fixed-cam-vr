@echo off
rem ---------------------------------------------------------------------------
rem  Start the operator desk (capture-server, :8099) at logon.
rem
rem  ASCII ONLY on purpose: cmd.exe reads .cmd as the ANSI codepage (cp932 here),
rem  so UTF-8 Japanese in a rem line gets mangled and the fragments run as
rem  commands. Keep every byte in this file ASCII.
rem
rem  Idempotent: onsite.py serve does nothing when :8099 is already listening.
rem  Two desks would roll each other's show.json back, so this guard matters.
rem
rem  Install : put a shortcut to this file in shell:startup
rem  Remove  : delete that shortcut (nothing else is modified)
rem  Note    : fires at LOGON only. A headless reboot stopped at the sign-in
rem            screen will not start the desk.
rem
rem  Why: if the desk dies mid-show nothing shows up on the headset -- the
rem  experience keeps running from the baked-in config, so sound / controller /
rem  eye-photo failures all go silent. See .claude/skills/onsite/SKILL.md
rem ---------------------------------------------------------------------------
cd /d "%~dp0.."
py -3.11 "tools\onsite.py" serve
