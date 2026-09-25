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

**Keeps an order.** Move items up and down in the window and the arrangement is
remembered on the component. Nothing in a finished menu survives between builds,
so each item is recorded by its parameter and value first and its label second;
an item the recording does not mention was added by a tool since and keeps its
place at the end rather than disappearing.

**Changes its structure.** Make submenus of your own, and move any item —
including a whole submenu a tool made — into another menu, a tool's or yours.
Like the order, each change is recorded against where the tool put the item, so
it is found again in the next build; one that no longer finds its item is shown
in the window and skipped, and the item stays where its tool put it. Moving
things between menus is kept behind an *Edit structure* switch, so an ordinary
drag can only reorder; *Move to* in the right-click menu works either way.

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

The pass runs in NDMF's Transforming phase, after Modular Avatar. A tool that
adds menu items later than that — VRCFury, for instance, which hooks the VRChat
SDK rather than NDMF — is outside what this can reach, and its items will be
paged by whatever added them.

## License

MIT.
