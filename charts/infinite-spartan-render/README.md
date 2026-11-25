# infinite-spartan-render

Helm chart for the Infinite Spartan Render job API (headless Blender and Ekur).

- Install from `oci://ghcr.io/playgrifball/charts/infinite-spartan-render`; the chart is cosign-signed by `helm-chart.yml`.
- `data.claimName` is required: an existing PersistentVolumeClaim holding the Halo Infinite data, mounted read-only at `/data`.
- Argo CD can follow the `deploy/current` tag: it holds the chart plus `deployed-image.yaml`, which pins the image by digest.
