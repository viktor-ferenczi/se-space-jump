# Black Smoke Jump

A Space Engineers client plugin that replaces the jump drive effect with a black smoke teleport.

When the countdown starts, a cloud of thick black smoke gathers around the ship, with violet lightning crackling
inside it. By the time the ship jumps it is out of sight. The cloud stays behind and slowly breaks up, and the ship
comes out inside a cloud of the same smoke at the other end. An aborted jump tears the cloud apart.

The jump sounds are new as well, and the game's music goes quieter while a jump is heard. On board, the HUD message
says what the gate is doing in place of the usual countdown: Manifesting Gate, then In Transit from 4 seconds before
the jump, as the location markers flicker out, to 5 seconds after it, then Jump Complete. Both count down the seconds
to the end of the transit, 15 at the start of the countdown. The markers stay out until the ship arrives.

The smoke stays out of pressurized rooms, so from inside the ship you only see it through the windows. Inside the
cloud nothing of the outside shows through it: no planets, asteroids, stars or other ships, only at its edges while it
gathers and breaks up. Seen from outside, the cloud hides the ship completely before it jumps and as it arrives: its
hull, its lights and the flames of its thrusters go dark in the smoke, so nothing changes when the ship is gone.

In third person view the camera gets a smoke gate of its own while your ship jumps: it gathers around the camera a
moment after the ship's cloud and goes wherever the camera goes, so you look out into the smoke as from a window of
the ship. Your ship stays in view through a thin haze, with thick smoke all around beyond it and lightning flashing in
it; nothing outside the gate shows. The ship's own cloud shows too, thinner than from outside: you see it gather
around the ship and, at the other end, clear away from it. Move the camera into a sealed room of the ship and the gate is gone: you see the
ship's own cloud through the windows, as from the cockpit. Through a camera block on the hull the whole world turns
into fog instead, which fades away once the ship is out at the other end.

Inside the smoke the sun and the sky no longer light the ship: the purple flashes and the lightning do, and its own
lights. In third person the sunlight on the hull fades as the world outside does. Other ships that do not jump disappear in the smoke as the view closes, for anyone on board, and come back as
it opens.

The smoke is lit by the sun: in sunlight the outer edge of the cloud turns a dark grey, brighter on its sunny side and
brightest when you look at it towards the sun, while its inside stays black. The cloud stands out as well against black
space as against a bright nebula.

In multiplayer the plugin works on your own client only. You see and hear every jump near you, whether or not the
other players have the plugin.

## Settings

Open the plugin's settings from the Pulsar plugin list. You can change the colours of the lightning and flashes, the
size and density of the clouds, how far away clouds are drawn, how quiet the music gets during a jump, and whether
your third person camera gets its smoke gate (and a camera block its fog). If the effect is slow on your GPU, lower the smoke billboard count.

## Sounds

The gate opens with a rising whine and roars while it is open. From right before the jump it echoes: a roar now and
then from somewhere in the cloud, and inside the cloud a constant stream of wails, quieter than the lightning. The jump
itself has no sound of its own: only a low rumble sets in as the ship leaves and as it arrives, and the echoes fade with
the cloud. `Assets/SOURCES.md` lists the sounds and when each one plays. The echoes and the lightning are cut on
your machine the first time they are needed, from the plugin's roar and from Space Engineers' own thunder, so they are
not included here.

## License

MIT, see `LICENSE`.
