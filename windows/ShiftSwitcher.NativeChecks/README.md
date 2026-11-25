# Bounded Windows rapid-input driver

**Do not run without the owner's explicit permission for this Windows SendInput driver.**
Compiling the source does not grant permission to inject input. This tool is not shipped
with the product and does not start with Windows.

The only supported target is the foreground Chrome window titled
`Shift Switcher — isolated compatibility fixture - Google Chrome`, with the empty,
writable `Тестовый текст` or `Тестовый поиск` field focused by CUA at:
https://vhvost.com/shift-switcher/compatibility-windows-20261125.html .
CUA must verify that exact URL before launch. There must be no competing UI actor.

The driver never opens/activates windows, changes settings/layouts, uses the clipboard,
or sends arbitrary text/shortcuts. It checks window, process and focused UIA field
identity before every input pair and stops on a change. Caps Lock must be off and
no modifier may be held. Only three fixed scenarios and 10/25 ms are accepted:

| Scenario | Initial layout | Physical virtual keys | Expected correction |
|---|---|---|---|
| `en-phrase` | EN-US | `bkb vbh ` | `или мир ` |
| `ru-phrase` | RU | `hello world ` (produces `руддщ цщкдв `) | `hello world ` |
| `ru-repeat` | RU | `hello hello ` (produces `руддщ руддщ `) | `hello hello ` |

Automatic correction must already be enabled in the product. The driver checks
the corrected text and actual Chrome-thread language. After it exits, CUA separately
checks undo, the restored language and protection on an extra space. The driver
does not issue undo shortcuts or reset fields. A failed or interrupted run is not
retried without first inspecting the current field through CUA.

Actual SendInput dispatch intervals are recorded, including UIA guard overhead.
Safety metadata is fetched with one UIA CacheRequest per input pair; it still
checks process, field runtime ID, label, password/enabled flags and writable
ValuePattern. The guard does not read values of unrelated fields. Short bounded
spinning between events avoids changing Windows timer settings.
`functionalPassed` and `timingWithinTolerance` are separate. A run only exits 0
when both pass; timing outside requested interval -1/+5 ms is not presented as
10/25 ms proof. This is synthetic input, never physical-keyboard proof.

Build without running:

```powershell
dotnet publish windows/ShiftSwitcher.NativeChecks -c Release -r win-x64 --self-contained true -o release/native-checks/win-x64
```

After explicit permission, launch the built EXE with one scenario and interval,
with its console hidden so the prepared Chrome field keeps focus. For example,
arguments `en-phrase 10`. Each invocation writes a new `qa-*.json` beside the
driver EXE; no previous report is overwritten. Reports contain only the empty
field's subsequent fixed test strings, target identity, timing and results.
