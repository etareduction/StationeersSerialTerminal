# Norsec TTY-6 Serial Terminal — Device API

The TTY-6 (`StructureSerialTerminal`, built from `ItemKitSerialTerminal`) is a dumb
glass teletype: no processor, no storage — just a character display and a keyboard
controller wired to a 4-register memory-mapped UART. Any IC10 housing on the same
data network can drive it with `get`/`put`.

## Screen

- Fixed character grid, **40 columns × 20 rows** by default
  (BepInEx config `Terminal.Columns` / `Terminal.Rows`).
- The cursor advances on every printed character, wraps at the end of a row, and
  the screen scrolls up one row when a newline runs off the bottom.
- Full printable ASCII (32–126; codes 127–255 also print if the UI font has a
  glyph for them). The same content is shown on the in-world monitor and in the
  terminal window (click the screen to open; input line at the bottom).
- Screen contents, cursor and the input buffer survive save/load and are synced to
  all clients.

## UART registers (`get`/`put`)

`GetStackSize` reports 4. Reading or writing any other address faults the IC.

| Addr | Name   | `put` (write)                                        | `get` (read)                              |
|------|--------|------------------------------------------------------|-------------------------------------------|
| 0    | DATA   | Print one ASCII character (code 1–255)               | **Pop** next typed character, `0` if none |
| 1    | STRING | Print a packed string, up to 6 chars: `STR("HELLO ")`| **Peek** next typed character, `0` if none |
| 2    | COUNT  | — (faults)                                           | Characters waiting in the input FIFO       |
| 3    | CTRL   | Command, see below                                   | Status bits, see below                     |

The IC10 `clr` instruction (clear device stack) resets the whole terminal:
clears the screen, discards the input FIFO and clears the overflow flag.

### CTRL commands (`put term 3 <code>`)

| Code | Constant             | Effect                              |
|------|----------------------|-------------------------------------|
| 1    | `CTRL_CLEAR_SCREEN`  | Clear the screen, cursor to home    |
| 2    | `CTRL_FLUSH_INPUT`   | Discard the input FIFO, clear overflow |
| 3    | `CTRL_CLEAR_OVERFLOW`| Clear the overflow flag only        |

### CTRL status (`get r? term 3`)

| Bit | Value | Meaning                        |
|-----|-------|--------------------------------|
| 0   | 1     | At least one input char waiting |
| 1   | 2     | Input buffer has overflowed     |

### Control characters (DATA writes)

| Code | Char | Effect                                    |
|------|------|-------------------------------------------|
| 8    | `\b` | Backspace: move cursor back, erase cell    |
| 10   | `\n` | Newline: cursor to start of next row       |
| 12   | `\f` | Form feed: clear screen, cursor to home    |
| 13   | `\r` | Carriage return: cursor to column 0        |

Other codes below 32 are ignored. Codes above 255 are ignored.

## Logic variables (`l`/`s`)

| LogicType  | Access | Meaning                                                        |
|------------|--------|----------------------------------------------------------------|
| `Setting`  | RW     | Write: print a packed string (same as `put 1`). Read: last value written |
| `Quantity` | R      | Characters waiting in the input FIFO (same as COUNT)           |
| `Error`    | R      | 1 while the input buffer has overflowed                        |
| `On`       | RW     | Device power switch                                            |
| `Power`    | R      | 1 when powered                                                 |
| `RequiredPower`, `PrefabHash`, `ReferenceId`, `NameHash` | R | Standard device values |

## Player input

- Click the monitor ("Open Terminal") to open the terminal window; type a line and
  press Enter. The line plus a trailing `\n` (10) is appended to the input FIFO.
- The terminal **never echoes locally** — nothing appears on the glass unless the
  circuit prints it. An interactive program should echo popped characters back to
  DATA (see the loop below).
- Non-ASCII characters are replaced with `?`.
- FIFO capacity is **256 characters**. On overflow, new characters are dropped and
  the overflow flag/`Error` is set (sticky until CTRL 2 or 3).

## Minimal example

```ic10
alias term d0
define ADDR_DATA 0
define ADDR_STR 1
define ADDR_CTRL 3
define CTRL_CLEAR_SCREEN 1
define CH_LF 10

s term On 1
put term ADDR_CTRL CTRL_CLEAR_SCREEN
put term ADDR_STR STR("READY.")
put term ADDR_DATA CH_LF

loop:                       # echo everything the player types
yield
l r0 term Quantity
blez r0 loop
get r1 term ADDR_DATA       # pop one char
put term ADDR_DATA r1       # print it back
j loop
```
