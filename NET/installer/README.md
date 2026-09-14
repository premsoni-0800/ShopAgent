# Installer

Builds `PrintlyPrintAgent-<version>-x64.msi` — the Windows installer for the
.NET print agent.

## Building one

```powershell
.\installer\build-installer.ps1
```

That publishes the agent and builds the MSI from *that* publish, in one step, so
you cannot ship an installer built from a stale publish directory. The result
lands in `NET\dist\`.

While iterating on the installer itself, `-SkipPublish` reuses whatever is in
`NET\publish\` rather than re-publishing 157 MB each time.

### What you need first

The WiX toolset, as a .NET global tool, plus two extensions:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
```

**Version 5 on purpose.** WiX 6 and later require accepting the Open Source
Maintenance Fee licence, which is a paid commercial arrangement and not a
decision the build should make quietly. Version 5 is the last MS-RL release.
If you do take out an OSMF licence, later versions should build this unchanged.

### Checking one

```powershell
wix msi validate .\dist\PrintlyPrintAgent-1.0.0-x64.msi
```

Runs the ICE suite. It should print nothing at all. Several of the rules it
enforces are about component key paths and are easy to get subtly wrong in ways
that only show up as a shortcut that will not uninstall, so treat any output as
a failure.

To check the payload without installing anything:

```powershell
msiexec /a .\dist\PrintlyPrintAgent-1.0.0-x64.msi /qn TARGETDIR=C:\some\empty\dir
```

An administrative install extracts the files and touches nothing else — no
registry, no shortcuts, no uninstall entry.

## What it does to a machine

| | |
|---|---|
| Installs to | `C:\Program Files\Printly Print Agent\` |
| Shortcuts | Start Menu (under "Printly") and Desktop |
| Starts at login | Yes — `HKLM\...\CurrentVersion\Run` |
| Uninstall | Add or Remove Programs, as "Printly Print Agent" |
| Scope | Per machine; needs elevation |

It starts at login because an agent that is not running prints nothing and
gives no sign of it — a shop that reboots the counter PC and does not notice
would have paid orders sitting unprinted with no visible cause. Closing the
window *does* stop the agent, so this is the difference between "closed by
accident until someone reopens it" and "closed until the next reboot". To turn
it off, untick Printly Print Agent in Task Manager's Startup tab.

Upgrading over a running agent is handled: the installer sends the window a
close message and waits, rather than finding its files locked and asking for a
reboot. That is an ordinary shutdown, so the database closes cleanly and a job
is not interrupted mid-print.

## The older Kotlin agent

A machine may still have **PrintlyAgent 0.1.0** installed at
`C:\Program Files\PrintlyAgent\` — the Java build this one replaces.

This installer does not touch it. It cannot: an MSI can only replace a product
it shipped, and that one was packaged by jpackage under its own identity.

**Uninstall it before or after installing this.** They are separate programs
with separate credentials and separate data directories, and nothing stops both
from running and both claiming jobs for the same shop. Remove it from Add or
Remove Programs, where it appears as "PrintlyAgent".

## Files here

| File | |
|---|---|
| `PrintlyAgent.wxs` | The package definition |
| `License.rtf` | Shown on the first page of the installer |
| `build-installer.ps1` | Publish, then build, in that order |

The version in Add or Remove Programs is read from `PrintlyAgent.csproj` at
build time, so it cannot drift from the assembly version. To release a new
version, change `<Version>` there and nothing here.

`UpgradeCode` in `PrintlyAgent.wxs` must never change. It is the only thing that
lets a future MSI recognise an installed copy as the same product and upgrade it
in place; change it and the next version installs alongside the old one instead.
