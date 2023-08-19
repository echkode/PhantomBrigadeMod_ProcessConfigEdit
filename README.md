# ProcessConfigEdit

**This mod is no longer needed.** The game has been patched by the publisher to fix a number of issues with processing config edit files.

A library mod for [Phantom Brigade](https://braceyourselfgames.com/phantom-brigade/) that works around an unintentional limitation with ConfigEdit processing.

The built-in ModManager has a mode that can edit the game's configuration files using YAML files that contain terse descriptions of the changes. The descriptions use a path syntax to target fields on the data objects being modified. Unfortunately, there's a limitation to the depth of field that can be targeted. Where this limitation is most prevalent is attempting to change the parents field on a part preset.

This mod lifts that limitation and adds a few enhancements. These enhancements let you craft complex edits like the following, if you are so crazy as to do so.

```
edits:
- 'parents.1: !d'
- 'parents.1.key: wpn_root_secondary !+'
- 'genSteps: !d'
- 'genSteps.0: !AddHardpoints !+'
- 'genSteps.0.subsystemsInitial: !d'
- 'genSteps.0.subsystemsInitial.0: wpn_main_assault_01 !+'
- 'genSteps.0.hardpointsTargeted.0: internal_main_equipment !+'
- 'genSteps.0.checks: !d'
- 'genSteps.0.checks.0: !CheckPartTag !+'
- 'genSteps.0.checks.0.requireAll: true'
- 'genSteps.0.checks.0.filter: !d'
- 'genSteps.0.checks.0.filter.difficulty_hard: true'
- 'genSteps.1: !AddHardpoints !+'
- 'genSteps.1.subsystemsInitial: !d'
- 'genSteps.1.subsystemsInitial.0: wpn_main_assault_02 !+'
- 'genSteps.1.hardpointsTargeted.0: internal_main_equipment !+'
- 'genSteps.1.checks: !d'
- 'genSteps.1.checks.0: !CheckPartTag !+'
- 'genSteps.1.checks.0.requireAll: true'
- 'genSteps.1.checks.0.filter: !d'
- 'genSteps.1.checks.0.filter.difficulty_easy: true'
```

As a bonus, I've thrown in an extra console command named `eq.save-part-preset` to save a part preset to a YAML file for free! You can use this to double check a complex ConfigEdit to make sure it's doing the right thing. Give the command the key of part preset and it'll save a YAML file named after the part preset key in the mod directory. You can also give the command an additional argument which is a path to an alternate save location if you don't want to save the file to the mod directory.
