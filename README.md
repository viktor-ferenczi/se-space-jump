# Black Smoke Jump

A Space Engineers client plugin that replaces the jump drive effect with a black smoke teleport.

When the countdown starts, a cloud of thick black smoke gathers around the ship, with violet lightning crackling
inside it. By the time the ship jumps it is out of sight. The cloud stays behind and slowly breaks up, and the ship
comes out inside a cloud of the same smoke at the other end. An aborted jump tears the cloud apart.

The jump sounds are new as well, and the game's music goes quieter while a jump is heard. On board, the HUD message
says what the gate is doing in place of the usual countdown: Manifesting Gate, then In Transit from 5 seconds before
the jump to 5 seconds after it, then Jump Complete. The location markers flicker out until the ship arrives.

The smoke stays out of pressurized rooms, so from inside the ship you only see it through the windows. Inside the
cloud nothing of the outside shows through it: no planets, asteroids, stars or other ships, only at its edges while it
gathers and breaks up. Seen from outside, the cloud hides the ship completely before it jumps and as it arrives: its
hull, its lights and the flames of its thrusters go dark in the smoke, so nothing changes when the ship is gone.

In third person view, and through a camera block on the hull, the camera goes into the void with your ship while it
jumps, from the start of the countdown. The whole world turns into fog with lightning flashing in it: what is outside
the gate fades away until nothing of it shows, and comes back the same way once the ship is out at the other end. Your
ship is in the fog with you, lit by the flashes: the parts near the camera show clearly, the farther ones dimmer, so
zooming in brings it out of the fog bit by bit. Other ships that do not jump disappear in the smoke as the view closes,
for anyone on board, and come back as it opens. Move the camera into a sealed room of the ship and you see the smoke
through its windows, as from the cockpit.

The smoke is lit by the sun: in sunlight the outer edge of the cloud turns a dark grey, brighter on its sunny side and
brightest when you look at it towards the sun, while its inside stays black. The cloud stands out as well against black
space as against a bright nebula.

In multiplayer the plugin works on your own client only. You see and hear every jump near you, whether or not the
other players have the plugin.

## Settings

Open the plugin's settings from the Pulsar plugin list. You can change the colours of the lightning and flashes, the
size and density of the clouds, how far away clouds are drawn, how quiet the music gets during a jump, and whether
your third person camera is wrapped in smoke. If the effect is slow on your GPU, lower the smoke billboard count.

## Sounds

The gate opens with a rising whine and roars while it is open. From right before the jump it echoes: a roar now and
then from somewhere in the cloud, and inside the cloud a constant stream of wails, quieter than the lightning. The jump
itself has no sound of its own: only a low rumble sets in as the ship leaves and as it arrives, and the echoes fade with
the cloud. `Assets/SOURCES.md` lists the sounds and when each one plays. The echoes and the lightning are cut on
your machine the first time they are needed, from the plugin's roar and from Space Engineers' own thunder, so they are
not included here.

## License

MIT, see `LICENSE`.
