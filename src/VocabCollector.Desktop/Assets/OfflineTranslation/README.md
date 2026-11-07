# Bundled English-to-Simplified-Chinese model

The four original inference files in `en-zh/` are the unmodified, decompressed
Mozilla Firefox Translations **Release / base-memory** model selected from the
[official model registry](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json)
generated on **2026-11-06 at 00:56:30 UTC**. This is a split-vocabulary model;
both `srcvocab.enzh.spm` and `trgvocab.enzh.spm` are required.

`models.manifest.json` pins the exact upstream URLs, archive and original-file
SHA256 checksums, sizes, registry timestamp, and license source revision. Every
`files[].path` is relative to this `OfflineTranslation` directory. The main
model's original-file SHA256 also matches `uncompressedHash` in Mozilla's
registry. The remaining checksums were calculated from the official downloads.

The original files total **49,913,927 bytes**; their upstream gzip archives total
**36,745,493 bytes**. These totals exclude the inference runtime. Only the four
original files are checked into Git, so builds and installed copies can use the
model without downloading it. The gzip archives are not release assets.

From the repository root, verify the bundled files without network access:

```powershell
./scripts/get-offline-translation-models.ps1 -VerifyOnly
```

Restore missing or damaged files from the pinned official sources:

```powershell
./scripts/get-offline-translation-models.ps1
```

The script verifies both archive and decompressed checksums before replacing
any file. Files already matching the manifest are retained. A failed download
or checksum does not replace existing files. `-OutputDirectory` can restore a
separate copy; it is the directory that will contain `en-zh/` and the license.
The script does not select a newer model from the live registry. Model updates
require an explicitly reviewed manifest change and matching runtime tests.

## License and upstream source

Mozilla distributes these model files under **Mozilla Public License 2.0**, as
stated in the [upstream README at the recorded revision](https://github.com/mozilla/translations/blob/8eada038fd45bbf6fa58a412b22f80c79887fabc/README.md).
The complete, unchanged license text is included as `LICENSE.MPL-2.0.txt`.

The original upstream model files can be obtained from each `sourceUrl` in
`models.manifest.json`, using the script above. Mozilla's model-generation
source and documentation are available in
[mozilla/translations](https://github.com/mozilla/translations).
Retain this source notice, the manifest, and the MPL license when redistributing
the models with TUCI. No additional restrictions are imposed on these model
files by this application.
