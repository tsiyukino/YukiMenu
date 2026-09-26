# Changelog

## [0.1.1] - 2026-09-26

- Build warnings are reported to NDMF through TsiYuki Core's `YukiNdmfReport` instead of this package's own
  copy. The messages are unchanged. Requires TsiYuki Core 0.4.0.
- Structure: make your own submenus, and move items and whole submenus between
  menus — into a tool's submenu, out of one, or into one of your own. Recorded
  against where each tool put the item, so it holds across builds.
- Structure: moving between menus is behind an *Edit structure* switch in the
  tree toolbar, off whenever the window opens; right-click → *Move to* works
  without it. Drags start only after the pointer has moved a little, show a
  blue line within a menu and an orange one naming the menu when changing menu,
  and open a closed submenu when rested on.
- Structure: moved items are marked, with *Move back* in the right-click menu.
  Submenus you made can be renamed, given an icon, and taken apart (the contents
  move up to where the submenu was).
- Structure: a *Structure changes* panel lists every change with a way back, and
  flags any that no longer find their item; the build reports those too.
- The window now reads the menu without Yuki Menu's own pass and lays the order
  and structure over it itself, with the same code the build uses, so changes
  show immediately without reading again.
- Every menu's pages are folded back before changes are applied, so an item that
  happened to be on a later page is found.
- Window: one spacing scale throughout; type tags and details line up in columns
  down the whole tree.
- Window: the settings fold away to a one-line summary, and the read button shows
  whether the result is up to date, out of date or failed.
- Window: rows show the kind of control as a coloured tag, highlight on hover,
  and show their move arrows on hover; right-click for move to top/bottom and copy.
- Window: pages are drawn as labelled bands, with the next-page link shown where
  the build will put it.
- Window: search by label or parameter.
- Settings and inspector show translated option names instead of code names.
- Fixed: control types showed as numbers (101, 102...) instead of their names.
- Fixed: rich text in menu labels is shown as VRChat shows it.

## 0.1.0

First release.

- A window showing the avatar's finished menu, read back from a real build, with
  drag-and-drop ordering and submenus closed to start with.
- Paging at a chosen limit of 2-8 items per page, replacing Modular Avatar's and
  VRCFury's, for the root wheel or for every submenu.
- Runs after VRCFury, so its items are included and its paging is replaced
  rather than replacing ours.
- A remembered order per menu, matched across builds by parameter and label.
