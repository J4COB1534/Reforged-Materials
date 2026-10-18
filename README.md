# Reforged Materials Redux

This is a full rewrite of my old Reforged Materials mod. It lets you paint and texture almost
any part in the game, straight from a panel in the editor or in flight: colours, real shine
and metals, weathering and dirt, and a big library of surface textures, put wherever you want on the part.

The main thing this time around: parts stay completely stock until you actually paint them.
Nothing changes on load, and it works on nearly any part, stock or modded.


## Requirements
- KSP 1.8 - 1.12.x
- ModuleManager
- TexturesUnlimited (this is what does the shiny PBR finishes)
- Deferred (optional)

## Install
Drop the `ReforgedRedux` folder into your `GameData` folder.

## Good to know
- Heat tiles give the part extra heat tolerance while they're on, and add a little weight
  (about 8 kg per square metre they cover). You can turn the weight and the extra heat tolerance off in Settings.
- Painted textures are saved in `GameData/ReforgedRedux/PluginData/BakeCache` so crafts load
  faster next time. If a painted part ever looks wrong, use Clear paint cache in Settings.
- Reset to stock is a real reset. The mod keeps each part's original materials safe and never
  changes a part for good.

## Add your own textures
You can drop in your own, no config needed. Put an image (png or jpg) into
`GameData/ReforgedRedux/Weather/` and it shows up in the Dirt tab automatically, named from the
filename. Want surface depth too? Put a normal map next to it with the same name plus `_n`, for
example `mytexture.png` and `mytexture_n.png`.

One thing that matters: use an OpenGL normal map, not a DirectX one, or the lighting and
reflections come out wrong. Most sites offer both, so grab the one marked OpenGL / GL / OGL. Any file format (with or without an alpha channel) works, the mod sorts that out for you.

Good free places to get textures (all CC0, free to use):
- ambientCG.com
- freepbr.com

## Credits
Made by me, J4COB. Most of the textures come from ambientCG.com (CC0). Thanks to Shadowmage
for TexturesUnlimited and sarbian for ModuleManager, both needed to run this.

## License
MIT, so do what you like with it, including forking it or continuing it if I ever stop working
on it. Just keep the credit. The textures are CC0 (public domain). See the LICENSE file for
the details.

If you want to throw me a coffee: https://buymeacoffee.com/j4c0b

## Building from source
The plugin is a single C# file (`Source/ReforgedRedux.cs`) compiled against KSP's own
assemblies - no external build system needed.

1. In `build/build.rsp`, replace `<PATH-TO-KSP>` with your own KSP install path.
2. Compile with the .NET Framework 4.x C# compiler:

   `csc.exe @build/build.rsp`

3. Put the resulting `ReforgedRedux.dll` in `GameData/ReforgedRedux/Plugins/`.

You also need ModuleManager and TexturesUnlimited installed to run it (see Requirements).
The weathering/texture image files are not in this repo (they are large binary assets);
they ship in the release download.
