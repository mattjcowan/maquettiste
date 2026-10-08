# HTTP API

The editor's API, as the [OpenAPI contract](api/openapi.yaml) describes it: every endpoint the editor and scripts use, its
parameters, its answers and the role it needs. Tests check the real endpoints against this contract, so it is the reference.

<div id="redoc"></div>
<script src="https://cdn.redoc.ly/redoc/v2.1.5/bundles/redoc.standalone.js"></script>
<script>
  Redoc.init("api/openapi.yaml", { hideDownloadButton: false, nativeScrollbars: true }, document.getElementById("redoc"));
</script>
