# Abyss Drives
Abyss Drives is a client plugin for Space Engineers, inspired by Elite Dangerous.
It replaces the jump drive effect with a black smoke teleport: a cloud with violet lightning gathers around the ship during the countdown and hides it, and the ship comes out of the same smoke at the other end.
From inside the ship the world outside turns into a dark abyss until the jump is over, and the HUD shows what the gate is doing in place of the countdown.

Install Abyss Drives through [Pulsar](https://github.com/SpaceGT/Pulsar) by selecting it in the Plugins tab.

## Using it
The effect is automatic: every jump near you gets it, including aborted jumps and a new jump started while the last one's smoke is still around.

It also works in multiplayer, since it runs on your own client only.
You see every jump near you whether or not the other players have the plugin, though in PvP the smoke is a slight self-nerf.

You can change the colors, the size of the clouds, how far away they are drawn and how quiet the music gets during a jump in the plugin settings.

## Building
Use a Pulsar DevFolder with `BlackSmoke.xml`. Building `AbyssDrives.sln` deploys the plugin to Pulsar's local plugin folder
only if `Pulsar` is set in `Directory.Build.props.user` or passed as `-p:Pulsar=...`.

## Support
Report bugs on the [Pulsar Discord](https://discord.gg/z8ZczP2YZY).
