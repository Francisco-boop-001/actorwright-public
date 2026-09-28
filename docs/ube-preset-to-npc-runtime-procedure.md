# UBE RaceMenu preset to NPC runtime procedure

## Authority

This is the user-confirmed reference procedure for reproducing a UBE RaceMenu
`.jslot` on a new NPC when conventional static FaceGen conversion does not
preserve likeness.

Chel v0.9 proved this route qualitatively at runtime on 2026-07-26. The
accepted mechanism and exact hashes are frozen in:

the historical consumer-owned record `accepted-chel-ube-runtime-mechanism-v0.9.json`

The procedure is a reusable starting point, not universal visual authority.
Every new preset still requires its own dependencies, Step Zero, package
verification, fresh-actor runtime test, and environment fingerprint.

## Why this route exists

Three static Chel routes failed to produce the preset likeness:

1. a Manager-synthesized UBE FaceGeom carrier;
2. an identity-skeleton variation; and
3. direct consumption of a complete user-exported RaceMenu CharGen carrier.

Those artifacts could be structurally valid and hash-correct while Skyrim still
rendered the same non-Chel face. The successful route stops treating translated
NPC FaceGen as the sole likeness authority. It lets RaceMenu interpret the
original `.jslot` in game through MDNR while NPC Manager still owns the actor,
plugin, static assets, evidence, and package.

## Required components

- NPC Manager new-NPC build route.
- UBE and the exact UBE custom-race plugin used by the preset.
- RaceMenu.
- MDNR - Mu Dynamic NPC Replacer 2.1.4.
- OBody Next Generation when a per-NPC BodySlide preset is required.
- Every plugin and loose/archive provider named by the JSlot's headparts,
  textures, race, hair, eyes, brows, lashes, mouth, and skin.
- A K-local copy of the exact JSlot and BodySlide SliderPreset XML.

MDNR is an external dependency and is not redistributed by the Manager package.

## Intake

1. Run dependency enablement against the exact JSlot and selected MO2 profile.
2. Snapshot the live environment.
3. Load the preset on the female player using the exact UBE race.
4. Confirm that the player appearance is acceptable before authoring the NPC.
5. Inventory the JSlot, tint source, UBE race, all seven or more headparts,
   BodySlide XML, body/hands/feet meshes, embedded texture routes, and providers.

Stop if the preset is not visually reachable in the selected environment.

## Manager host

Create a schema 6 new-NPC host unless another independently justified feature
requires a later schema.

The host must:

- use the exact UBE race and sex;
- preserve the selected headpart FormKeys;
- package the Manager FaceGeom and FaceTint as static fallback/evidence;
- carry the Manager runtime apply VMAD and PEX;
- avoid BodyGen when OBody owns the body;
- avoid a schema 7 private WNAM/body-mesh route when the accepted UBE race skin
  should remain inherited; and
- set `allowInheritedMeshEmbeddedSkinTextureRoute: true` when UBE naked-skin
  ARMAs omit female TXST.

The inherited route is accepted only after the Manager hash-binds the winning
loose body, hands, and feet NIFs, reads their embedded DDS paths, and closes
every texture provider. Missing NIFs or textures remain hard refusals.

## MDNR rule

Bundle the exact JSlot and qualified tint under the installed SKSE tree, then
target only the created actor base:

```json
{
  "actors": [
    {
      "condition": "IsActorBase(ChelNpcManager.esp|0x800)",
      "priority": 100,
      "inserttype": "unique",
      "volatile": false,
      "presets": [
        {
          "race": {
            "formid": "0x05A18E",
            "plugin": "UBE_AllRace.esp"
          },
          "gender": "female",
          "applytype": {
            "overrides": false,
            "bodymorphs": false,
            "transforms": false,
            "skinoverrides": false
          },
          "presetfile": "CharGen\\Presets\\UBE_Chel.jslot",
          "tintfile": "CharGen\\ChelNpcManager.dds",
          "bodypresetfile": "CalienteTools\\BodySlide\\SliderPresets\\Hourglass Body UBE.xml"
        }
      ]
    }
  ]
}
```

Replace the actor, race, paths, and hashes for each project. Do not reuse Chel's
FormKeys or tint by name.

## One owner per runtime channel

Chel's successful ownership split is:

| Channel | Owner |
|---|---|
| NPC identity and static record | NPC Manager |
| UBE JSlot face and headparts | MDNR through RaceMenu |
| JSlot overlays and accepted head transform | Manager VMAD |
| BodySlide preset | OBody |
| Body/hands/feet skin and textures | inherited UBE race authority |

For this split, keep all four MDNR `applytype` switches false. Enabling them can
stack MDNR node overrides, JSlot body morphs, transforms, or skin overrides on
top of Manager/OBody routes and produce a different result.

Another project may choose a different owner, but it must name exactly one
owner for each channel and prove that no duplicate route is active.

## BodySlide and OBody

Inspect and hash the exact XML before packaging. Bind:

- preset name;
- target `set`;
- groups;
- ordered slider rows; and
- SHA-256.

The XML remains in BodySlide's native percentage units. Do not normalize its
values into BodyGen units.

Set `bodypresetfile` to that XML and let OBody apply it to the actor. Disable
JSlot BodyGen/body-morph output so the two shapes do not stack. UBE outfits
still need generated UBE meshes and compatible `.tri` morph files to follow the
same OBody preset.

## Package and runtime test

1. Verify the Manager package and every declared file.
2. Run package gates with the candidate's explicit appearance profile.
3. Independently reopen the plugin, FaceGeom, FaceTint, JSlot, MDNR JSON,
   BodySlide XML, and archive.
4. Confirm no unwanted BodyGen, private body, private WNAM, or alternate
   BodySlide authority entered the package.
5. Disable older versions and install MDNR plus the new package.
6. Use a fresh actor/save and capture the untouched face before
   `setnpcweight`.
7. Inspect the MDNR log first if the JSlot does not apply.
8. Capture a post-test environment fingerprint and bind the verdict to the
   exact archive and appearance hashes.

## Accepted-baseline rule

After a user accepts the result, create a normalized baseline contract that
freezes:

- package archive;
- plugin's appearance-bearing NPC subrecords;
- FaceGeom and FaceTint;
- exact JSlot and tint copy;
- MDNR JSON;
- Manager runtime PEX;
- BodySlide XML;
- inherited UBE body/hands/feet provider closure; and
- successful environment fingerprint.

Later outfit, inventory, AI, behavior, placement, and follower work must branch
from that baseline. Those changes may add their own records and fields, but
must prove the protected appearance surface unchanged or explicitly requalify
every affected visual layer.

## Known non-conclusions

- A user-confirmed perfect likeness without retained screenshots is a valid
  qualitative baseline, but not complete formal visual authority.
- A static MDNR JSON proves configuration, not that MDNR ran.
- A valid UBE BodySlide project does not prove its generated outfit meshes or
  OBody `.tri` output exist.
- An actor with no TPLT, DOFT, CNTO, OTFT, or PKID is barebones, but those
  absences alone do not prove a CTD cause.
- Inventory and AI failures do not invalidate an independently accepted visual
  baseline.
