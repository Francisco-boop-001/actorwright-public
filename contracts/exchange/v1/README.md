# Actorwright exchange protocol v1

This protocol is the only routine bridge between the Actorwright product repository and a mod-work repository. Bundles are immutable directories containing `bundle-manifest.json` plus exactly the files declared by that manifest.

The four payload kinds are `issue`, `response`, `release-candidate`, and `promotion`. A response binds the SHA-256 of the incoming issue manifest; a promotion binds the candidate manifest. All paths are forward-slash relative paths. Reparse points, undeclared files, duplicate JSON keys, path traversal, secret-like fields, non-redistributable artifacts, and source/credential payloads are refused.

Validation is two-sided: Actorwright uses `tools/exchange/validate_bundle.py`; the Skyrim consumer uses its own implementation and shares only these byte-identical schemas.
