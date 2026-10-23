# Black Smoke Jump

A Space Engineers client plugin that replaces the jump drive effect with a black smoke teleport.

When the countdown starts, a cloud of thick black smoke gathers around the ship, with violet lightning crackling
inside it. By the time the ship jumps it is out of sight. The cloud stays behind and slowly breaks up, and the ship
comes out inside a cloud of the same smoke at the other end. An aborted jump tears the cloud apart.

The jump sounds are new as well, and the game's music goes quieter while a jump is heard. On board, the HUD message
counts down to the jump in place of the usual one, and the location markers flicker out until the ship arrives.

The smoke stays out of pressurized rooms, so from inside the ship you only see it through the windows. Inside the
cloud nothing of the outside shows through it: no planets, asteroids, stars or other ships, only at its edges while it
gathers and breaks up.

In third person view the camera gets its own cloud while your ship jumps. Wherever the camera is, you see only
smoke and lightning until the ship comes out at the other end.

In multiplayer the plugin works on your own client only. You see and hear every jump near you, whether or not the
other players have the plugin.

## Settings

Open the plugin's settings from the Pulsar plugin list. You can change the colours of the lightning and flashes, the
size and density of the clouds, how far away clouds are drawn, how quiet the music gets during a jump, and whether
your third person camera is wrapped in smoke. If the effect is slow on your GPU, lower the smoke billboard count.

## Sounds

The jump sounds are based on recordings of Elite Dangerous; `Assets/Sounds/SOURCES.md` lists where each one comes
from. The lightning sounds are cut from Space Engineers' own thunder on your machine the first time they are needed, so
they are not included here.

## License

MIT, see `LICENSE`.
