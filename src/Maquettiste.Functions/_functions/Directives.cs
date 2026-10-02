// Package directives of the editor's functions (phase2-design.md section 3.1, PD19): the only file with #: lines. The committed
// version is the one in Directory.Build.props; the image's pack step rewrites the Maquettiste.Engine line to the per-build version
// its local NuGet feed holds (PD24). The host swaps StaticSiteHost.Abstractions for its own copy, whatever the version says.
#:package Maquettiste.Engine@0.5.3
#:package StaticSiteHost.Abstractions@*
