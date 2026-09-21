# Yuki Menu

See the menu your avatar actually ends up with, and decide its shape.

Every tool installs its own menu items by its own rules, and none of them can
see the result, because the result does not exist until a build has run. Yuki
Menu builds the avatar on a hidden copy, reads the finished menu back, and shows
you that — then lets you reorder it and set how it is paged.

## What it does

**Shows the real menu.** Not an estimate. The tree in the window comes from a
real build, with the paging undone, so you see the list of menu items rather
than the pages they happened to be split across. Your scene is not touched.

**Pages it your way.** VRChat allows eight controls on a wheel, and Modular
Avatar fills all eight before spilling into a `More` submenu. Eight is also the
point at which each wedge is at its smallest and hardest to hit in VR, so a
lower limit is often the more usable one. Yuki Menu undoes the existing paging
first, so its setting decides the result rather than layering on top of it.
Choose whether that applies to the root wheel only or to every submenu.

**Handles VRCFury.** VRCFury is not an NDMF plugin — it is a VRChat SDK
preprocessor that runs between NDMF's two halves — so it adds its items after
everything else has finished and pages the whole menu at eight again, with its
own `Next` links. Anything laid out before that point is undone. Yuki Menu runs
on the far side of it, in NDMF's Optimizing phase, where what it sees includes
VRCFury's items and is what ships. It recognises Modular Avatar's `More` links,
VRCFury's `Next` links and its own, including the ones VRCFury strands in the
middle of a menu by appending its items after them.

**Keeps an order.** Drag items into the order you want and it is remembered on
the component. Nothing in a finished menu survives between builds, so each item
is recorded by its parameter and value first and its label second; an item the
recording does not mention was added by a tool since and keeps its place at the
end rather than disappearing.

## Use

Add a **Yuki Menu** component anywhere in the avatar (`TsiYuki → Yuki Menu`, or
the button in the window), then open **TsiYuki → Menu Layout** and press *Read
the menu*.

| Setting | What it does |
| --- | --- |
| Applies to | Root wheel only, or every submenu. |
| Items per page | 2–8. Fewer means larger, easier wedges. |
| Take over paging | Off leaves the menu exactly as the other tools built it. |
| Next page label / icon / position | The control that leads to the next page. |

## Requires

Unity 2022.3, VRChat Avatars SDK 3.7+, NDMF 1.11+, Modular Avatar 1.12+, and
[Yuki Core](https://github.com/tsiyukino/YukiCore) 0.3+.

Install from the [TsiYuki VPM listing](https://tsiyukino.github.io/vpm-repos/).

## Notes

On an avatar with VRCFury, reading the menu runs the whole VRChat preprocessor
chain rather than NDMF alone — that is the only way to see VRCFury's items,
because it builds them itself. That takes around ten seconds instead of two, and
VRCFury writes its usual build trace to the console while it happens. The build
runs on a copy that is thrown away; nothing is uploaded and your scene is left as
it was. Avatars without VRCFury take the short path.

The layout pass itself runs in NDMF's Optimizing phase, after VRCFury. A tool
that adds menu items even later than that would be outside what this can reach.

## License

MIT.
