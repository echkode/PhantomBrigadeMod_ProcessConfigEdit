# ProcessConfigEdit

**This mod is no longer needed.** The game has been patched by the publisher to fix a number of issues with processing config edit files.

A library mod for [Phantom Brigade](https://braceyourselfgames.com/phantom-brigade/) that works around an unintentional limitation with ConfigEdit processing.

Sections

- [Context Token](#context-token)
- [Set Context Operation](#set-context-operation)

The built-in ModManager has a mode that can edit the game's configuration files using YAML files that contain terse descriptions of the changes. The descriptions use a path syntax to target fields on the data objects being modified. A field path is a dot-delimited string that represents chained member access on the target data object.

## Context Token

This mod adds some new tokens to the path syntax so that you can use a shorthand to refer to the same path in subsequent edit steps. There is the "context token" which is denoted with a `^` character (hat). The idea is that it points to the field path in the previous edit step. The context token can only appear as the first segment in a field path. The other new token is the "index glob token" which is denoted with a `*` character (splat). This can only appear in the last segment of a path.

The first field path segment can contain more than one context tokens. This lets you chain contexts together. If the first segment contains a context token and it is more than one character long, all the characters in the segment must be context tokens.

The index glob token is meant to be used when adding a new element to a list. This lets you append the element without having to know the list size. It can only be used when the field on the data object is a list.

Here is an example of a part preset config edit that uses both new tokens.
```
removed: false
edits:
- 'genSteps.*: !AddHardpoints !+'
- '^.subsystemsInitial: !d'
- '^^.0: internal_aux_strafe !+'
- '^.hardpointsTargeted.0: internal_aux_weapon !+'
- '^.priority: 15'
```

The first edit step uses the index glob token so that a new AddHardpoint genStep can be added. The next edit step uses the context token to acquire the field path of the new list element. The index is automatically resolved so that the context is pointing to the correct index. If you enable logging, you can see this in `Player.log`:
```
Mod 3 (TestEdits) applying edit to config wpn_rifle_assault_01 path genSteps.*
Mod 3 (TestEdits) edits config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.* | Adding new entry of type IPartGenStep to end of the list (fps idx: 1)
Mod 3 (TestEdits) resolves index in config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.* | List index is now 1 (fps idx: 1)
Mod 3 (TestEdits) assigns context in config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.1 | Context genSteps.1 (fps idx: 1) added | context level: 1
Mod 3 (TestEdits) edits config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.1 | Assigning new default object of type AddHardpoints to target index 1
```
The third edit step uses a double context token to chain contexts. That means this edit step is applied to the `subsystemsInital` field on the list element that was added in the first edit step. The context tokens are resolved to field path segments in `Player.log` so it's a good idea to turn on logging and examine the log to make sure it's targeting the field you want. Here you can see the expanded field path being assigned to the context.
```
Mod 3 (TestEdits) applying edit to config wpn_rifle_assault_01 path genSteps.1.subsystemsInitial
Mod 3 (TestEdits) assigns context in config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.1.subsystemsInitial | Context genSteps.1.subsystemsInitial (fps idx: 2) added | context level: 2
Mod 3 (TestEdits) edits config wpn_rifle_assault_01 of type DataContainerPartPreset, field genSteps.1.subsystemsInitial | Assigning new default object of type List`1 to target field
```
The fourth edit step drops a level of context so that it refers once again to the list element added in the first step. If you lost track of what the context is, you can just look at the column with the context token and follow it up until the character in that column is no longer a context token.

The context is set after both add and remove operations on lists and dictionaries as well as default values for objects with fields that can be edited. The example above shows the context being set after an add operation on a list. Here is an example with a remove operation in a unit preset config edit.
```
removed: false
edits:
- 'partTagPreferences.body.0.tags.mnf_vhc_01: !-'
- '^.tank_turret_aa: false !+'
- '^.tank_turret_adapter: false !+'
- '^.tank_heavy: true !+'
- '^.tank_elevated: false !+'
- '^.training: false !+'
```
The context after the remove operation is `partTagPreferences.body.0.tags` because this is the dictionary the remove operation is applied to.

The context token has some complicated rules about how it resolves field paths. Be sure to enable logging the first time you try out some new combination of field operation and context so you can see how it's resolving the path.

## Set Context Operation

The context token needs an existing edit step to refer to. Sometimes that's inconvenient so this mod adds a new operation to explicitly set the context. The keyword for the set context operation is `!^`. Here is an example subsystem config edit that uses the set context operation.
```
removed: false
edits:
- 'stats: !^'
- '^.act_count.value: 2'
- '^.act_duration.value: 0.9'
- '^.act_heat.value: 110'
- '^.mass.value: 5.5'
- '^.wpn_damage.value: 16.6'
- '^.wpn_concussion.value: 0'
- '^.wpn_impact.value: 0.01875'
- '^.wpn_impact_radius.value: 1'
- '^.wpn_range_min.value: 3'
- '^.wpn_range_max.value: 75'
- '^.wpn_scatter_angle.value: 4.5'
- '^.wpn_scatter_angle_moving.value: 6.75'
- '^.wpn_speed.value: 300'
```
The `stats` field itself isn't being modified in anyway so the set context operation is used to put it into context.
