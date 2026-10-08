---
hide:
  - navigation
  - toc
---

# HTTP API

The editor's API, as the [OpenAPI contract](api/openapi.yaml) describes it: every endpoint the editor and scripts use, its
parameters, its answers and the role it needs. Tests check the real endpoints against this contract, so it is the reference.

<div id="redoc"></div>
<script src="https://cdn.redoc.ly/redoc/v2.1.5/bundles/redoc.standalone.js"></script>
<script>
  // The page is served at api/, beside the contract (docs/api/openapi.yaml): MkDocs rewrites links, not script paths.
  Redoc.init("openapi.yaml", { hideDownloadButton: false, nativeScrollbars: true }, document.getElementById("redoc"));
</script>

<style>
  /* The contract's own three panes need the page's width: no sidebars, no width cap, no article padding around them. */
  .md-grid { max-width: none; }
  .md-content__inner { margin: 0 1rem; }
  #redoc { margin: 0 -1rem; }
</style>
