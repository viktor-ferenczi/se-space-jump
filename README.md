# Abyss Drives

A Space Engineers client plugin that replaces the jump drive effect with a black smoke teleport.

Jump drives now open an Elite Dangerous style gate to move the ship between two points:

- Clouds appear at both the entry and exit locations.
- Crew members see the outside environment become a dark abyss.
- Aborted and chained jumps are handled.

The plugin also works in multiplayer, though the smoke is a slight self-nerf for PvP. Its options can be adjusted in
the plugin settings.

## The gate

When the countdown starts, a cloud of thick black smoke gathers around the ship, with violet lightning crackling
inside it. By the time the ship jumps, the cloud hides it completely: its hull, its lights and the flames of its
thrusters go dark in the smoke, so nothing changes when the ship is gone. The cloud stays behind and slowly breaks up,
and the ship comes out inside the same smoke at the other end. An aborted jump tears the cloud apart.

The sun lights the smoke. In sunlight the outer edge of the cloud turns dark grey, brighter on its sunny side and
brightest when you look at it towards the sun, while its inside stays black. The cloud stands out against black space
as well as against a bright nebula.

## On board

The smoke stays out of pressurized rooms, so from inside the ship you only see it through the windows. Inside the
cloud nothing of the outside shows through: no planets, asteroids, stars or other ships, except at its edges while it
gathers and breaks up. Ships that do not jump disappear in the smoke as the view closes and come back as it opens.

Inside the smoke the sun and the sky stop lighting the ship. The sunlight fades out as the sun's disc disappears in
the smoke and comes back with it, in every view on board, and the shadows and reflections of the sky go and come back
with it. The ship is then lit by its own lights and by the purple flashes and the lightning. Each flash swells and
fades over about a second, some brighter or longer than others, and lights the ship and the smoke around it. Your eyes
do not adjust to the dark while you are in the smoke, so the ship looks as dark as it really is without the sun.

## Third person view and camera blocks

In third person view the camera gets a smoke gate of its own while your ship jumps. It gathers around the camera a
moment after the ship's cloud and goes wherever the camera goes, so you look out into the smoke as if from a window of
the ship. Your ship stays in view through a thin haze, with thick smoke and lightning all around beyond it, and nothing
outside the gate shows. You also see the ship's own cloud, thinner than from outside, gather around the ship and clear
away from it at the other end. Move the camera into a sealed room of the ship and the gate is gone: you see the ship's
cloud through the windows, as from the cockpit.

Through a camera block on the hull the whole world turns into fog instead, which fades away once the ship is out at
the other end.

## HUD messages

On board, the HUD message shows what the gate is doing in place of the usual countdown:

- Manifesting Gate while the gate forms.
- In Transit from 4 seconds before the jump, as the location markers flicker out, until they flicker back in 5 seconds
  after it.
- Jump Complete at the end.

The first two show the seconds left until Jump Complete, for example Manifesting Gate (15s) at the start of the
countdown.

Only those seated in the ship jump with it. Anyone else on or in the ship sees a red Jump Imminent (Ns) during the
countdown, with the seconds left until the jump. It shows as soon as you leave your seat and goes away when you sit down
again. An aborted jump shows Jump Aborted! in the same red. If a new jump starts while the last one's smoke is still
around the ship, the messages follow the new jump.

## Sounds

The gate opens with a rising whine and roars while it is open. From right before the jump it echoes: now and then a
roar comes from somewhere in the cloud, and inside the cloud there is a steady stream of wails, quieter than the
lightning. The jump itself has no sound of its own. A low rumble sets in as the ship leaves and again as it arrives, and
the echoes fade with the cloud. The game's music goes quieter while a jump can be heard.

The echoes and the lightning are not included in the plugin. They are cut on your machine the first time they are
needed, from the plugin's roar and from Space Engineers' own thunder.

## Multiplayer

The plugin runs on your own client only. You see and hear every jump near you, whether or not the other players have
the plugin.

## Settings

Open the settings from the Pulsar plugin list. You can change the colors of the lightning and the flashes, the size and
density of the clouds, how far away clouds are drawn and how quiet the music gets during a jump. You can also choose
whether your third person camera gets its smoke gate (and a camera block its fog) and whether ships jumping in near you
stay hidden until their cloud has formed. If the effect is slow on your GPU, lower the number of smoke billboards.

## License

MIT, see `LICENSE`.
