I've been doing my research on Inazuma Eleven Victor Heroes, and for anyone interested, here's how far I've gone. I'm absolutely terrible at modding, but I gave it a shot anyway. I'm really not sure about the result, but if it helps a real modder create this mod, that would be awesome :

Research summary: Multiple / “Unlimited” Custom Avatars in Inazuma Eleven: Victory Road
I have been researching whether it is possible to have multiple independently editable custom avatars/custom players in the same save and the same team in Inazuma Eleven: Victory Road.

The long-term goal would ideally be something like:

Custom Player A
Custom Player B
Custom Player C
Custom Player D
...
potentially 99+
with each one having its own appearance, portrait, stats, position, element, moves, playstyle, etc., rather than all of them being treated as the game's single global Avatar.

This is a summary of everything found so far.

1. Environment / tools used
The main research was done on PC build 5.0.1, with the relevant formation Lua being:

soccer_formation_menu_inc_6.00.34.00.lua
Tools used included:


Viola for CPK dumping/repacking

IEVR Mod Manager

custom PowerShell RAM scanners / patchers

static analysis of nie.exe

Lua analysis

x64dbg for a few runtime captures

a full dump of the game's CPK data
The full Viola dump contained approximately:

936 CPK files
~109.5 GB extracted
~255,303 files
Some relevant files found were:

chara_edit_1.03.75.00.cfg.bin
chara_edit_menu_5.00.03.00.lua.bin
chara_edit_parts_menu_status_1.03.71.00.lua.bin
chara_edit_parts_menu_status_ability_learning.lua.bin
kizuna_town_avatar_menu_5.00.32.00.lua.bin
soccer_formation_menu_inc_6.00.34.00.lua.bin
chara_edit_1.03.75.00.cfg.bin references resources such as:

mdl_edit_avatar01
...
mdl_edit_avatar16
but these appear to be editor-related presets/resources, not sixteen independent saved Avatar slots.

2. Important distinction: Avatar editor slot vs player instances
One of the first things we found is that the game's original Avatar system appears to be designed around essentially one active editor/avatar slot.

Earlier native analysis showed an Avatar-related structure where a compact handle and active index were stored around:

+0x15610
+0x15614
and the neighboring structure starts around:

+0x15618
So simply enlarging that single array in place would not be safe.

However, this turned out not to mean that only one Avatar-like player can exist as a character instance.

That distinction became the most important discovery of the project.

3. We successfully created two persistent Avatar player instances
Two test avatars were used:

Avatar A
= "Sacha"
= goalkeeper
= large male character with turquoise/blue braids

Avatar B
= female character with red hair
After RAM manipulation, both existed simultaneously as separate player instances.

Their persistent CharaStHdl values were:

Avatar A = 0x0000780D = 30733
Avatar B = 0x00017811 = 96273
These were not merely temporary UI objects.

They survived a game restart with the same handles.

This was our first strong proof that:

Victory Road can contain more than one player instance originating from custom Avatar data.

4. The real initial problem: both handles resolved to the same Avatar record
Even though A and B had different CharaStHdls, both originally resolved to the same internal Avatar identity.

We located the native conversion path around:

CharaStHdl -> internal Avatar key
RVA 0x00D08C40
and the Avatar record lookup around:

RVA 0x00CE32B0
Originally:

A 0x0000780D
    ↓
0x74BCAF5A

B 0x00017811
    ↓
0x74BCAF5A
So both ended up using the same Avatar record.

5. Avatar record table discovered
We identified what appears to be the main Avatar record table.

In the session where it was analysed:

32 records
record stride = 0xAB0
table begins around manager + 0x10
The visual/customization recipe is located approximately around:

record + 0xA47
At that point there were only a small number of occupied records and many unused records.

This was very interesting because it suggested that the backend already has substantially more storage than the normal one-Avatar UI exposes.

6. Major breakthrough: the Avatar records were successfully split
We cloned the original Avatar record into a free record and gave Avatar B another internal key.

The resulting routing became:

Avatar A
0x780D
↓
key 0x74BCAF5A
↓
Record A

Avatar B
0x17811
↓
key 0x74BCAF5B
↓
Record B
In that particular runtime session, the two record addresses were:

A = 0x206813406E0
B = 0x20681341190
Those addresses are of course session-specific RAM addresses; the important part is the independent routing and record structure.

The validator reported:

ROUTING DISTINCT: True
We then closed and reopened the formation screen and got:

ROUTING STILL DISTINCT AFTER REFRESH: True
The records also genuinely contained different appearance recipes.

The B record matched the B appearance data exactly, while the A record remained much closer to A.

So at this point we had demonstrated:

Two different player handles can route to two different internal Avatar keys and two different visual/customization records.

That is probably the most important result of the whole research so far.

7. But the formation UI still displayed the same Avatar face
Even after the backend was split successfully, the formation screen continued to show Avatar A's appearance/portrait for both A and B.

That showed that the game's formation UI has another Avatar-specific path downstream.

Lua analysis supported this.

UpdateMemberCharaIconInfoByChrStHdl deals separately with things such as:

charaStHdl
faceIconChrStHdl
isUpdateTempAvatar
So the UI can know the real player's handle while using another value for its portrait.

8. Formation structures were reverse engineered
Static/runtime analysis eventually gave us a much better picture of the formation object.

One native formation routine is around:

0x140FB3210
The formation uses records with a stride of:

0x80
From a row-local point of view we found fields corresponding approximately to:

row + 0x28 = CharaStHdl
row + 0x6C = face-icon selector
When viewed from the larger formation object's first-row layout, this corresponds to values around:

object + 0x98 = handle
object + 0xDC = selector
depending on which structure pointer is being used.

This distinction caused several false positives early in the research, but was eventually resolved by capturing the actual live objects.

9. The decisive face-icon result
Using a real live Avatar B formation entry, we obtained:

Avatar B
CharaStHdl = 0x00017811
selector   = 0x4858BE15
Avatar A had:

Avatar A
CharaStHdl = 0x0000780D
selector   = 0x4858BE15
So despite two different characters:

A handle != B handle
the UI was using:

A face selector == B face selector
This was reproduced again later in a completely clean/original state with broad RAM probes.

Strong formation candidates gave:

A → selector 0x4858BE15
B → selector 0x4858BE15
So this is no longer speculation.

It is experimentally confirmed.

10. We proved that this selector actually controls the portrait
We then changed only Avatar B's selector:

0x4858BE15
→
0x4858BE16
The result was immediate:

Avatar B's portrait became blank.

Avatar A was unaffected.

This proves that the selector field really is involved in selecting/resolving the generated Avatar face icon.

It also taught us something else:

The selector cannot simply be replaced with any arbitrary unique number.

0x4858BE16 had no corresponding prepared/generated portrait resource, so the UI displayed nothing.

11. Another attempted fix: using CharaStHdl as the selector
Static analysis located an important write around:

RVA 0x00FB1D73
with an instruction equivalent to:

mov dword ptr [r12+0x6C], r14d
A little later the same value participates in the Avatar icon path, including formatting associated with:

menuIconComp_avatar_%u_%u
We made an experimental native RAM patch so that instead of the original selector, each formation row would use its real CharaStHdl:

Avatar A → selector 0x0000780D
Avatar B → selector 0x00017811
The result:

Both Avatar portraits became blank.

Again this was useful.

It proved that simply having two unique selector numbers is insufficient.

The corresponding resource/name/cache entry also has to exist.

12. What we currently know about the portrait blocker
The confirmed situation is therefore:

A
CharaStHdl 0x780D
internal Avatar key A
Avatar record A
custom recipe A

B
CharaStHdl 0x17811
internal Avatar key B
Avatar record B
custom recipe B
The backend can distinguish them.

But farther downstream:

A -> face selector 0x4858BE15
B -> face selector 0x4858BE15
so the UI face-icon path collapses both characters back onto the same dynamic Avatar icon.

We have also confirmed that:

changing selector only
!=
creating a valid new Avatar portrait
because the matching dynamically generated resource/cache entry needs to exist as well.

13. Lua/mod-manager experiments
We also tested a static Lua modification using:

soccer_formation_menu_inc_6.00.34.00.lua
The mod was repacked with Viola and successfully installed through IEVR Mod Manager.

The mod manager itself successfully applied the mod.

This confirmed that modifying/replacing the formation Lua through the normal mod pipeline is viable.

However, changing the high-level Lua side alone did not solve the fundamental dynamic face-icon identity/cache issue.

So the blocker appears to extend into the native/resource path.

14. x64dbg experiments
We briefly used x64dbg to capture the exact live formation objects.

This gave useful results, particularly the exact B object/selector relationship.

However, Victory Road loads many modules and callbacks and the debugger caused frequent freezes/interruptions.

For that reason we decided not to base the eventual solution on interactive debugging.

The current preferred native approach is:

static nie.exe analysis
+
small targeted RAM instrumentation
rather than requiring x64dbg for users.

15. Where the original/native approach currently stops
Immediately before changing direction, the next objective was:

Capture the source value used to generate 0x4858BE15 separately for Avatar A and Avatar B, before the face-icon resource is created.

A no-debugger trace was being developed specifically for this.

The idea was to instrument the native code around FB1D73, record the relevant registers for A and B, then restore the original instruction automatically.

That investigation is not finished.

So if we continue the original method, this is approximately where we resume.

16. Possible route A: continue modifying the game's native Avatar system
This is the original approach.

The remaining work would approximately be:

1. Find exactly where the dynamic Avatar face-icon identity is generated.

2. Make it depend on the real Avatar/player instance instead of the single global Avatar identity.

3. Ensure that both:
   - the generated portrait resource
   - and the formation selector
   use the same unique identity.

4. Validate:
   A portrait != B portrait

5. Make the A/B Avatar record split automatic.

6. Allocate:
   key
   record
   CharaStHdl
   face-icon identity
   automatically for every custom Avatar.

7. Test:
   2 → 4 → 8 → 16 → 32

8. Investigate the current 32-record table limit.

9. If necessary:
   enlarge it
   or virtualise/swizzle Avatar records
   to target 99+.
Advantages
We already know a lot about this path.

Most importantly, we have already proven that multiple Avatar-like player instances and multiple independent visual records work.

Disadvantages
The original game's Avatar implementation contains several assumptions that appear designed around one global/custom Avatar.

Even after splitting the backend record, secondary systems such as portrait generation still collapse back to the shared Avatar path.

There may be several more systems like this.

17. New possible route B: create custom players instead of multiple “Avatars”
A community member suggested another idea:

Instead of trying to make the game support multiple instances of its one special Avatar ID, create an external editor similar to the Echoes of Aincrad character editor.

The editor would expose the Avatar creator configuration in a GUI, then generate a new character with a different identity/ID.

This is a very interesting direction.

A modern Echoes of Aincrad save editor now supports appearance editing and duplication of characters into available slots, with duplicated characters receiving their own identities rather than appearing as duplicate instances of the same character.

The equivalent concept for Victory Road would be something like:

IEVR Custom Player Editor

[New Custom Player]

Name
Gender
Voice
Hair
Face
Eyes
Colors
Body
Position
Element
Stats
Moves
Playstyle
etc.

          ↓

Generate Custom Player #0001
Generate Custom Player #0002
Generate Custom Player #0003
...
Instead of:

Avatar
Avatar
Avatar
Avatar
the game would ideally see:

CustomPlayer_0001
CustomPlayer_0002
CustomPlayer_0003
CustomPlayer_0004
Each would then have its own:

character/base identity
player instance
CharaStHdl
appearance recipe
save record
18. Important limitation of the save-editor idea
A save editor alone may not be sufficient.

There is already public reverse engineering of Victory Road saves. One current open-source save editor documents that character records use a literal chara_base_id originating from the game's chara_base_*.cfg.bin data, and that its editor cannot simply invent a nonexistent character ID in the save.

That does not make the idea impossible.

It suggests that the final system may need to be hybrid:

CUSTOM PLAYER EDITOR
        +
GAME DATA MOD
For example:

1. Reserve/create valid custom chara_base entries in modded game data.

2. Give every custom character a real unique base ID.

3. Use an external editor to create/edit their save instances.

4. Store the Avatar appearance recipe for each one.

5. Teach the game, if necessary, that those normal custom players
   may use Avatar/customization components.

6. Let normal character systems handle portraits, formation,
   save identity, etc. whenever possible.
This may completely bypass some of the special-case logic that currently assumes there is only one Avatar.

Victory Road already has community save editors capable of parsing and rewriting meaningful portions of the save format, so this route would not necessarily require reverse-engineering the entire save format from zero.

19. Why the new approach is attractive
The native Avatar route currently looks approximately like:

multiple CharaStHdls
↓
multiple Avatar records
↓
single special Avatar UI logic
↓
shared portrait/cache identity
The Custom Player route would instead try to achieve:

Custom Player A → unique base identity → Avatar recipe A
Custom Player B → unique base identity → Avatar recipe B
Custom Player C → unique base identity → Avatar recipe C
So instead of repeatedly fixing every place where the game assumes:

"this is THE Avatar"
we try to make the game think:

"these are three different characters"
while still allowing those characters to use Avatar-style customization.

This might ultimately scale much better.

20. First tests I would perform for the Custom Player route
Before building a GUI, we should prove the architecture with the smallest possible experiment.

Test 1 — Clone a normal player
Take one ordinary player instance from the save and identify:

chara_base_id
instance/player ID
CharaStHdl
level
position
element
stats
etc.
Duplicate it using another valid character identity and see whether both coexist.

Test 2 — Give the duplicate independent appearance data
Determine whether the duplicate can reference/use an Avatar visual recipe.

If:

Player A → normal appearance
Player B → custom Avatar recipe B
works simultaneously, the new architecture becomes extremely promising.

Test 3 — Two fully custom players
Create:

Custom A
Custom B
each with a different valid base identity and different Avatar recipe.

Put both in the same formation.

Test 4 — persistence
Verify after:

save
return to title
restart game
reload save
that A and B remain independent.

Test 5 — scale
Then:

2
→ 4
→ 8
→ 16
→ 32
→ 64
→ 99+
instead of immediately trying 99.

21. What is already proven vs what is not
Confirmed
We have experimentally demonstrated:

✔ Two persistent CharaStHdl player instances can exist.

✔ Avatar A and Avatar B can coexist in the same team.

✔ Their handles can be different.

✔ They can route to different internal Avatar keys.

✔ They can route to different Avatar records.

✔ Those records can contain genuinely different visual recipes.

✔ The split survives a formation refresh.

✔ The formation UI has a separate face-icon identity path.

✔ A and B currently receive the same face selector:
      0x4858BE15

✔ Changing B's selector changes/breaks only B's displayed portrait.

✔ Therefore the face-icon selector is a real part of the blocker.

✔ Arbitrary unique selectors are not enough:
   a matching generated/cached portrait resource is required.
Not yet proven
We have not yet proven:

✘ Truly unlimited Avatars.

✘ 99+ custom players.

✘ That all 32 discovered Avatar records are safely usable simultaneously.

✘ That a completely new arbitrary chara_base_id can simply be added to a save.

✘ That a normal character can already use an Avatar recipe without additional code.

✘ That the save-editor/custom-player route definitely avoids every Avatar-specific limitation.

✘ A permanent final mod.
So I would currently describe the project as:

Multiple Avatar/player instances and independent visual records are proven possible. The remaining problem is how Victory Road assigns identity to the dynamic Avatar rendering/portrait path, or alternatively how to move custom appearances onto normal uniquely identified characters.

22. Current proposed research direction
At this point I think both approaches are worth keeping.

Route A — continue native Avatar reverse engineering
Resume at the dynamic face-icon generation path and solve the remaining single-Avatar assumptions.

This has the advantage that much of the underlying work is already done.

Route B — investigate the Custom Player Editor idea
Study the save + chara_base relationship and see if we can create a system in which every created character receives its own normal game identity while using Avatar customization data.

If Route B works, it may be considerably cleaner and more scalable than fighting every special case in the original Avatar system.

23. What would be especially useful from other modders
If anyone in the community has information about any of these, it would be extremely useful:

- chara_base_*.cfg.bin structure
- creating additional valid chara_base entries
- player instance IDs / CharaStHdl generation
- character records in USERDATALIVE
- Avatar appearance recipe serialization
- how normal player portraits are generated
- dynamic menuIconComp_avatar resources
- the native path around 0x00FB1D73 / 0x00FB3210
- character duplication through the save
- whether anyone has already made a custom character use Avatar parts
The most important thing is that we are past the question of whether two Avatar-derived player instances can coexist.

They can.

The research now concerns how to give every one of those instances a completely independent identity across all systems, or how to bypass the special Avatar system entirely by turning them into real custom player records.