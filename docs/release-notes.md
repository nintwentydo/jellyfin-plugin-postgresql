PostgreSQL 1.1.4.0 requires [Jellyfin v12.0.0-nintwentydo.1](https://github.com/nintwentydo/jellyfin/releases/tag/v12.0.0-nintwentydo.1). It is not compatible with stock Jellyfin 12. The bundled image is `ghcr.io/nintwentydo/jellyfin-postgres:1.1.4.0-jf12.0.0-nintwentydo.1`.

This release completes generated PostgreSQL IDs inside the built-in ZIP restore transaction and adds six regression tests. It also includes the pending native backup/recovery, TLS and PostgreSQL 15–18 validation improvements.

Install the attached ZIP with the matching fork, or use the bundled image. The existing plugin catalogue and Docker `latest` tag are unchanged. Back up the database and Jellyfin files together before upgrading; see the [operations guide](https://github.com/nintwentydo/jellyfin-plugin-postgresql/blob/master/docs/operations.md) for ZIP restore limitations and recovery.
