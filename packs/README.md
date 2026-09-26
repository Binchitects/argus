# Pack library

Built knowledge packs (`*.arguspack`) go here: `tools/build-packs.sh` writes
them, and **Admin → Packs** lists them to load. Loading links a pack into
Argus's own packs folder, so nothing is copied and unloading loses nothing.

The folder is mounted read-only into Argus as `/pack-library`. To keep packs
elsewhere, set `ARGUS_PACK_LIBRARY_DIR` in `deploy/.env` to that folder.

Nothing here but this file is committed: packs are hundreds of MB.
