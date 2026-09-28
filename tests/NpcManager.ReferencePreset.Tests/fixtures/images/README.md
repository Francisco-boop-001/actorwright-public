# Reference image fixtures

The focused suite creates controlled 1-pixel PNG, JPEG, and WebP files under
the project's disposable `03-builds/work` root. Real native inference uses the
three existing, K-local Elvira views under
`projects/ElviraFromPreset/sources`; those source images are not duplicated in
the test tree.

The test project never reads from or writes to the protected live modlist.
