---
title: Achievement images
description: Upload, get, list, and download XBOX achievement images.
---

# Achievement images

`xbox xbl achievement-image` uploads PNG images for a title's achievements and reads back the
images registered for it. `--scid` takes the title's service configuration identifier (SCID).
`upload` returns an `iconImageId` to put in the title's achievement configuration.

| Command | Effect |
| --- | --- |
| `achievement-image upload` | Upload a new image and register it for the SCID. |
| `achievement-image get` | Look up one registered image. |
| `achievement-image list` | List the images registered for a SCID. |
| `achievement-image download` | Save full images and thumbnails under `--dest`. |

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | all | Yes | SCID of the XBOX title. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults as [Context](../context.md) describes, then to your only seller. |
| `--file <path>` | `upload` | Yes | Local `.png` file, exactly **1920 × 1080 pixels** and at most **20 MiB**. |
| `--asset-id <guid>` | `get`, `download` | For `get`; one of `--asset-id` or `--all` for `download` | Asset ID returned by `upload` or found with `list`. |
| `--all` | `download` | Alternative to `--asset-id` | Download every image registered for the SCID. |
| `--dest <directory>` | `download` | Yes | Save each image and its thumbnail in a subfolder named for its asset ID. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

## Examples

Upload an image:

```bash
xbox xbl achievement-image upload \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --file achievement-1920x1080.png
```

Pass the `assetId` that `upload` returns to verify the image, and to download it and its
thumbnail into `achievement-images/<assetId>/`:

```bash
xbox xbl achievement-image get \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --output json-compact

xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --dest achievement-images
```

List every image registered for the SCID, and download them all:

```bash
xbox xbl achievement-image list \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --output table

xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --all --dest achievement-images --output table
```

## Output

`upload` and `get` return one image object, and `list` returns an array of them, or `[]`.
Each object has the service's fields, such as `assetId`, `cdnUrl`, `thumbnailCdnUrl`,
`widthInPixels`, and `heightInPixels`, plus `iconImageId`, which equals `assetId`:

```json
{
  "assetId": "11111111-2222-3333-4444-555555555555",
  "iconImageId": "11111111-2222-3333-4444-555555555555"
}
```

Copy `iconImageId` into the title's `achievements2017.xml` configuration:

```xml
<IconImageId>11111111-2222-3333-4444-555555555555</IconImageId>
```

Their tables show `ASSET ID`, `CDN FILENAME`, `WIDTH`, `HEIGHT`, `IMAGE TYPE`, and `PUBLIC`.
Use JSON for the full CDN URLs and other fields.

`download` returns a manifest: an object with `assetId`, `destination`, `imagePath`,
`thumbnailPath`, `imageBytes`, and `thumbnailBytes` for `--asset-id`, or an array of them for
`--all`. Its table shows `ASSET ID` and `DESTINATION`. Files with the same names are replaced,
and other files in the asset folder are kept. If a download fails, that asset's existing files
are left as they were; with `--all`, folders already downloaded stay.

A failed command exits 1 and writes the error to stderr. When the service rejects the image,
the error lists each validation message.

## Production safety and upload recovery

Every upload registers a new image, even of the same file, and the CLI never retries one. If
`upload` reports `Upload outcome unknown`, the image might have been registered: run the `get`
or `list` command it prints on stderr before uploading again, and use the existing asset ID if
you find your image. Ctrl+C before the registration is sent registers nothing.

To inspect the full image and thumbnail, download them:

```bash
xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --dest achievement-images
```

`upload` doesn't ask before it registers an image, and `--confirm` doesn't apply to it. See
[Safety and destructive operations](../../safety.md).
