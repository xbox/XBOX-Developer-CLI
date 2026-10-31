---
title: XBOX Live achievement images
description: Upload, get, list, and download XBOX Live achievement images.
---

# XBOX Live achievement images

`xbox xbl achievement-image upload` **creates and registers** a PNG image for
one XBOX Live title. `--scid` identifies the title's service configuration.
Upload returns the registered `assetId` and its `iconImageId` for achievement
configuration. Use `get` to verify a known image and `list` to view all registered images.
`download` saves full images and thumbnails locally.

| Command | Effect |
| --- | --- |
| `achievement-image upload` | Upload a new image to storage and register it. **Mutates production state.** |
| `achievement-image get` | Look up one registered image. Read-only. |
| `achievement-image list` | List registered images for a SCID. Read-only. |
| `achievement-image download` | Save full images and thumbnails under `--dest`. Reads remote data and writes local files. |

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | all | Yes | Nonzero service configuration ID of the XBOX Live title. |
| `--seller-id <id>` | all | No | Authorized positive seller ID; otherwise uses configured or saved selection, then the developer's only authorized seller. |
| `--file <path>` | `upload` | Yes | Local PNG with a `.png` filename, exactly **1920 × 1080 pixels**, at most **20 MiB (20,971,520 bytes)**. The CLI checks extension, file size, and PNG header dimensions before upload; the service validates the image content. |
| `--asset-id <guid>` | `get`, `download` | For `get`; one of `--asset-id` or `--all` for `download` | Nonzero asset ID returned by `upload` or found with `list`. |
| `--all` | `download` | Alternative to `--asset-id` | Download every registered image for the SCID. |
| `--dest <directory>` | `download` | Yes | Save each image and its thumbnail in a subfolder named for its asset ID. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

See [XBOX Live](../xbl.md#shared-behavior) for seller selection and
other shared behavior.

## Examples

```bash
xbox xbl achievement-image upload \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --file achievement-1920x1080.png

# Use the assetId returned by upload to verify the image.
xbox xbl achievement-image get \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --output json-compact

# View all registered images for this SCID
xbox xbl achievement-image list \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --output table

# Download one image and thumbnail into achievement-images/<assetId>/.
xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --dest achievement-images

# Download all registered images for a title.
xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --all --dest achievement-images --output table
```

Upload, get, and list add `iconImageId` to each image's CLI JSON response,
equal to the registered `assetId`. For example, a response contains:

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

`upload` and `get` return one image JSON object; `list` returns a bare array of
those objects (or `[]` when empty). `list` and `download --all` follow every
continuation token and return every image in one array. Response fields include
`assetId`, `scid`, `cdnUrl`, `thumbnailCdnUrl`, `widthInPixels`, `heightInPixels`,
`imageType`, `isPublic`, `imagePreviewUrl`, and `thumbnailPreviewUrl`; the CLI retains
all supplied fields and adds `iconImageId`
for the XML. Use `assetId` from `list` with `get` or `download` for the same
SCID and seller. On failure, no success-shaped JSON is emitted; errors go to
stderr with a nonzero exit. When the response includes validation errors,
the CLI includes their keys and messages in stderr JSON. Request-wide
messages use the empty-string key in `errors`. Upload HTTP 400 responses
without validation errors include the image requirements.
Their tables show `ASSET ID`, `CDN FILENAME` (the decoded filename from the full
image's `cdnUrl`), `WIDTH`, `HEIGHT`, `IMAGE TYPE`, and `PUBLIC`. Upload and get
show one row; list shows one row per image, or `No results.` when empty. Use JSON
or `json-compact` for full CDN URLs and other response fields.

`download` returns a JSON manifest: one object with `assetId`, `destination`,
`imagePath`, `thumbnailPath`, `imageBytes`, and `thumbnailBytes` for
`--asset-id`, or an array of those objects for `--all`. Its table shows
`ASSET ID` and `DESTINATION`; an empty inventory returns `[]` in JSON or
`No results.` in table format. The CLI downloads full images and thumbnails
from their CDN URLs without sending your access token. Each transfer
is limited to 20 MiB and two minutes, with up to five redirects. Both files
download before either is saved, so a failed transfer leaves existing files
untouched. Existing files with the same CDN filename are replaced; other files
in the asset folder are preserved. With `--all`,
completed asset folders remain if a later download fails.

## Production safety and upload recovery

Uploading registers a production image, so agents need explicit user approval first;
the PlayFab `--confirm` gate does not cover it. The CLI never retries registration
after a timeout or temporary failure. If it reports `Upload outcome unknown`, run the
read-only command it prints on stderr before uploading again.

To inspect the full image and thumbnail, use:

```bash
xbox xbl achievement-image download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 \
  --asset-id 11111111-2222-3333-4444-555555555555 --dest achievement-images
```

See [Safety and destructive operations](../../safety.md).
