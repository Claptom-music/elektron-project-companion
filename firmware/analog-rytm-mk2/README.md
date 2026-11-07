# Analog Rytm MKII storage layout registry

The companion reads a tagged layout ID from the blank `.arprj` template. When the user checks the registry, it requests `<layout-id>.json` in this folder. Each filename uses eight lowercase hexadecimal digits without `0x`. The file is a storage lockfile or a build manifest containing a lockfile under `store`.

To contribute a layout from a public firmware build, open a pull request with the build's lockfile. Include a link to the build source and describe how you confirmed the layout ID against a project exported from the build. Share metadata only and keep your `.arprj` backup and firmware image local.
