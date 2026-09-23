# Changelog

## Unreleased

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
