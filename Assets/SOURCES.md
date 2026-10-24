# The sounds

| File | Played |
|---|---|
| `spoolup.wav` | the gate opening: once at the cloud when the countdown starts |
| `roar.wav`, `roar.far.wav` | the open gate's roar: a 10 s loop at the cloud while it lasts, near and far (no highs), crossfaded |
| `burst.wav` | the jump's rumble: once at the cloud left behind and once at the exit, from the jump on |
| `tear-discharge.wav` | an aborted jump: once at the cloud torn apart |

From right before the jump to the end of the cloud the gate echoes: now and then a roar sounds somewhere in the cloud,
and inside the cloud there is a constant stream of wails. Both are cut from `roar.wav` and `roar.far.wav` on the
player's machine the first time they are needed (`ClientPlugin/Core/EchoCuts.cs`), and kept in
`Storage/BlackSmoke/Echoes` in the game's user data folder: the wails are short stretches of the roar with its low drone
filtered out, the echoes longer swells of the whole roar, each repeating itself a few times, quieter each time, and
played a few semitones higher or lower at random.

The lightning is not in this folder either. The plugin cuts it from Space Engineers' own weather lightning
(`Content/Audio/ARC/EVENT/Lightning_0.wav` and `Lightning_2.wav`) on the player's machine and keeps the cuts in
`Storage/BlackSmoke/Lightning/se` in the game's user data folder.

# The textures

`SoftDisc.dds`: the round billboard behind the smoke's cover (`ClientPlugin/Effects/SoftDisc.cs`), 128x128 uncompressed
32 bit BGRA, white, opaque out to 0.85 of its radius and a smoothstep down to nothing at its rim. Made by the plugin
itself (it wrote it into the temp folder at first use until #1435); shipped here so it writes nothing outside the game's
user data folder.
