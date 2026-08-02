# Norsec TTY-6 Serial Terminal — Device API

The TTY-6 (`StructureSerialTerminal`, built from `ItemKitSerialTerminal`) is a dumb
glass teletype: no processor, no storage — just a character display and a keyboard
controller wired to a 6-register memory-mapped UART. Any IC10 housing on the same
data network can drive it with `get`/`put`.

## Screen

- Fixed character grid, **40 columns × 20 rows**.
- The cursor advances on every printed character, wraps at the end of a row, and
  the screen scrolls up one row when a line feed runs off the bottom.
- Full printable ASCII (32–126; codes 128–255 except 133 also print if the UI font
  has a glyph for them). The same content is shown on the in-world monitor and in
  the terminal window (click the screen to open).
- Screen contents, cursor, transfer modes and the input buffer survive save/load;
  the screen is synced to all clients.

## UART registers (`get`/`put`)

`GetStackSize` reports 6. Reading or writing any other address faults the IC.

| Addr | Name   | `put` (write)                                        | `get` (read)                               |
|------|--------|------------------------------------------------------|--------------------------------------------|
| 0    | DATA   | Print — one char, or a packed string in buffered mode | **Pop** input — one char, or up to 6 packed in buffered mode; `0` if none |
| 1    | STRING | Print a packed string, up to 6 chars: `STR("HELLO ")`| **Peek** next typed character, `0` if none |
| 2    | COUNT  | — (faults)                                           | Characters waiting in the input FIFO        |
| 3    | CTRL   | Command, see below                                   | Status bits, see below                      |
| 4    | ROW    | Move cursor to row (clamped to 0–19)                 | Current cursor row                          |
| 5    | COL    | Move cursor to column (clamped to 0–39)              | Current cursor column                       |

The IC10 `clr` instruction (clear device stack) resets the whole terminal:
clears the screen, discards the input FIFO, clears the overflow flag and returns
both transfer modes to unbuffered.

### Transfer modes

Input and output each have an independent transfer mode, set via CTRL commands.
**Both default to unbuffered.**

- **Unbuffered** (default): DATA moves one character per `get`/`put`.
- **Buffered**: DATA moves one packed ASCII-6 string per `get`/`put` — a write
  unpacks and prints up to 6 characters (same as STRING), a read pops up to 6
  waiting characters and returns them packed (first typed character in the
  highest byte, the same layout `STR("...")` produces). Reads still return `0`
  when the FIFO is empty.

Modes only affect the DATA register; STRING, COUNT, CTRL, ROW and COL are unchanged.

### CTRL commands (`put term 3 <code>`)

| Code | Constant                | Effect                                 |
|------|-------------------------|----------------------------------------|
| 1    | `CTRL_CLEAR_SCREEN`     | Clear the screen, cursor to home       |
| 2    | `CTRL_FLUSH_INPUT`      | Discard the input FIFO, clear overflow |
| 3    | `CTRL_CLEAR_OVERFLOW`   | Clear the overflow flag only           |
| 4    | `CTRL_OUTPUT_UNBUFFERED`| DATA writes print one char (default)   |
| 5    | `CTRL_OUTPUT_BUFFERED`  | DATA writes print a packed string      |
| 6    | `CTRL_INPUT_UNBUFFERED` | DATA reads pop one char (default)      |
| 7    | `CTRL_INPUT_BUFFERED`   | DATA reads pop up to 6 chars, packed   |

### CTRL status (`get r? term 3`)

| Bit | Value | Meaning                         |
|-----|-------|---------------------------------|
| 0   | 1     | At least one input char waiting |
| 1   | 2     | Input buffer has overflowed     |
| 2   | 4     | Output is in buffered mode      |
| 3   | 8     | Input is in buffered mode       |

### Control characters (DATA/STRING writes)

| Code | Char | Effect                                                       |
|------|------|--------------------------------------------------------------|
| 8    | `\b` | Backspace: cursor left one column, stops at column 0          |
| 10   | `\n` | Line feed: cursor down one row, **column unchanged**; scrolls |
| 12   | `\f` | Form feed: clear screen, cursor to home                       |
| 13   | `\r` | Carriage return: cursor to column 0                           |
| 127  | DEL  | Destructive backspace: cursor left one column and erase that cell (`BS SP BS` in one code); does nothing at column 0 |
| 133  | NEL  | Next line: carriage return + line feed in one code            |

Other codes below 32 are ignored. Codes above 255 are ignored. For a full
newline print NEL (133), or CR then LF — a bare LF keeps the column, like a real
teletype.

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

- Click the monitor ("Open Terminal") to open the terminal window. Input is
  **unbuffered**: every keystroke goes straight into the input FIFO as it is
  typed — there is no input line and no local editing.
- **Enter sends CR (13)** — like a real terminal keyboard. **Backspace sends
  BS (8).** A program that wants line editing must interpret those itself
  (e.g. echo DEL (127) to rub out a character, NEL (133) on Enter).
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
define CH_BS 8
define CH_CR 13
define CH_DEL 127
define CH_NEL 133

s term On 1
put term ADDR_CTRL CTRL_CLEAR_SCREEN
put term ADDR_STR STR("READY.")
put term ADDR_DATA CH_NEL

loop:                       # echo everything the player types
yield
l r0 term Quantity
blez r0 loop
get r1 term ADDR_DATA       # pop one keystroke
bne r1 CH_CR notcr
move r1 CH_NEL              # Enter arrives as CR: echo a full newline
notcr:
bne r1 CH_BS notbs
move r1 CH_DEL              # Backspace: echo a destructive rubout
notbs:
put term ADDR_DATA r1       # print it back
j loop
```
