using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G4Composition;

// Consume the already validated retail export, but publish mergeable framework
// declarations instead of replacing the game's complete character catalogs.
internal static class CharacterFantasyExporter
{
    static readonly string[] BaseFields = "id,name,serial_no,name_text,short_name_text,upper_name_text,model,motion_override,reserved_8,voice_bank,kind,gender,chara_class,academic_year,c14,series,team_1,team_2,team_3,description_text,age_group,sort_order,in_ie1,in_ie2,in_ie3,in_go1,in_go2,in_go3,in_ares,in_orion,in_victory_road,flag_31,flag_32,reserved_33".Split(',');
    static readonly string[] ModelFields = "id,skeleton_path,body_mesh_path,motion_override,body,clothes,shoes,gloves,parts_color,c9,head_mesh_path,head_texture_path,collar_open,equipment_flag_2,reserved_14,c15,skin_color,color_17,exmesh_1,exmesh_2,reserved_20,reserved_21,color_22,flag_23,flag_24,flag_25,color_26,exmesh_3,reserved_28,flag_29,flag_30,chest_size,c32,force_kit,custom_parts_path,flag_35,facial_motion".Split(',');
    static readonly string[] AuraFields = "id,key,name_text,desc_text,power_min,power_max,linked_skill,aura_chara,c8,c9,c10,c11,event,chara_param,reserved_14,reserved_15,order,flag_17,flag_18".Split(',');
    static string Q(string value) => Newtonsoft.Json.JsonConvert.ToString(value ?? "");
    static string Id(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    static string Value(object value) => value is string text ? Q(text) : value == null ? Q("") : Convert.ToString(value, CultureInfo.InvariantCulture);
    static bool Same(CfgTable.Row a, CfgTable.Row b) => a != null && b != null && a.Name == b.Name && a.Types.SequenceEqual(b.Types) && a.Values.SequenceEqual(b.Values);
    static void Write(string root, string path, StringBuilder text)
    {
        string destination = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.WriteAllText(destination, text.ToString(), new UTF8Encoding(false));
    }
    static string Find(string data, string folder, string prefix) => Directory.GetFiles(Path.Combine(data, "common", "gamedata", folder), prefix + "*.cfg.bin").Single();
    static CfgTable Read(string path) => CfgTable.Read(File.ReadAllBytes(path), path);
    static CfgTable Native(string data, string path) => Read(Path.Combine(DumpDataManager.CommonPath, path.Substring(Path.Combine(data, "common").Length + 1)));
    static CfgTable.Row[] Rows(CfgTable table, string name) => table.Rows.Where(r => r.Name == name).ToArray();
    static string Cells(CfgTable.Row row, CfgTable.Row original, int start = 1, params int[] skip) => "{ " + string.Join(", ", Enumerable.Range(start, row.Values.Length - start)
        .Where(i => !skip.Contains(i) && (original == null || i >= original.Values.Length || !Equals(row.Values[i], original.Values[i])))
        .Select(i => "col_" + i + " = " + Value(row.Values[i]))) + " }";

    internal static void WriteResources(CharacterMakerDocument[] documents, string[] resolvedIds, string data, string root)
    {
        if (documents.Length != resolvedIds.Length) throw new InvalidDataException("Fantasy character result count mismatch.");
        string basePath = Find(data, "character", "chara_base_");
        string modelPath = Find(data, "character", "chara_model_");
        string paramPath = Find(data, "character", "chara_param_");
        var bases = Read(basePath); var models = Read(modelPath); var parameters = Read(paramPath);
        var nativeBase = Native(data, basePath); var nativeModels = Native(data, modelPath); var nativeParams = Native(data, paramPath);
        var languageTables = new Dictionary<string, CfgTable>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder("schema = \"VRFW.character/2\"\ndefault_lang = \"en\"\n");
        for (int i = 0; i < documents.Length; i++) {
            var doc = documents[i];
            var row = Rows(bases, "CHARA_BASE_INFO").Single(r => r.Text(1) == resolvedIds[i]);
            var original = Rows(nativeBase, "CHARA_BASE_INFO").Single(r => r.Text(1) == doc.TemplateId);
            var model = Rows(models, "CHARA_MODEL_INFO").Single(r => r.UInt(0) == row.UInt(6));
            var originalModel = Rows(nativeModels, "CHARA_MODEL_INFO").Single(r => r.UInt(0) == original.UInt(6));
            string entry = doc.IsNew ? "character" : "edit";
            text.Append("\n[[").Append(entry).Append("]]\n");
            if (doc.IsNew) text.Append("name = ").Append(Q(resolvedIds[i])).Append("\nbased_on = ").Append(Q(doc.TemplateId)).Append('\n');
            else text.Append("target = ").Append(Q(resolvedIds[i])).Append('\n');
            // Fantasy recruits through the player album as well as the retail
            // soul shop. Its default recruitment price is independent of the
            // SHOP_INFO_ITEM_CONSUME price, so the free option must set both.
            if (doc.freeSoulInShop) text.Append("\n[").Append(entry).Append(".recruit]\nprice = 0\nrequires = []\ntrials = []\n");
            // Do not pin global serial / sort indexes allocated by the retail writer.
            text.Append("\n[").Append(entry).Append(".base]\n");
            for (int n = 0; n < row.Values.Length; n++) {
                if (n == 0 || n == 1 || n == 2 || n == 21 || n == 6 || !doc.IsNew && Equals(row.Values[n], original.Values[n])) continue;
                text.Append(BaseFields[n]).Append(" = ").Append(Value(row.Values[n])).Append('\n');
            }
            if (doc.IsNew) {
                // Fixed identifiers preserve existing saves across subsequent exports.
                text.Append("id = ").Append(Id(row.UInt(0))).Append("\nname = ").Append(Q(row.Text(1))).Append('\n');
                text.Append("\n[character.new_model]\nid = ").Append(Id(model.UInt(0))).Append("\nfrom = ").Append(Id(originalModel.UInt(0)))
                    .Append("\n[character.new_model.cells]\n");
                AppendCells(text, model, originalModel);
            }
            else {
                if (row.UInt(6) != original.UInt(6)) text.Append("model = ").Append(Id(row.UInt(6))).Append('\n');
                text.Append("\n[edit.model_row]\n"); AppendCells(text, model, originalModel);
            }
            foreach (var card in Rows(parameters, "CHARA_PARAM_INFO").Where(r => r.UInt(1) == row.UInt(0))) {
                var templateCards = Rows(nativeParams, "CHARA_PARAM_INFO").Where(r => r.UInt(1) == original.UInt(0)).ToArray();
                CfgTable.Row template;
                if (doc.IsNew) {
                    var variant = (doc.identity?.techniques ?? new List<CharacterTechniqueSettings>()).SingleOrDefault(v =>
                        card.UInt(0) == (v.parameterId == doc.identity.techniqueParameterId
                            ? G4Runtime.G4TextureParser.Crc32("pc_para_" + resolvedIds[i])
                            : G4Runtime.G4TextureParser.Crc32("pc_para_" + resolvedIds[i] + "_" + v.parameterId.ToString("X8"))));
                    template = templateCards.Single(r => r.UInt(0) == (variant?.parameterId ?? throw new InvalidDataException("Fantasy card template missing.")));
                } else template = templateCards.Single(r => r.UInt(0) == card.UInt(0));
                if (!doc.IsNew && Same(card, template)) continue;
                text.Append("\n[[").Append(entry).Append(".cards]]\nid = ").Append(Id(card.UInt(0))).Append('\n');
                if (doc.IsNew) text.Append("from = ").Append(Id(template.UInt(0))).Append('\n');
                text.Append("cells = ").Append(Cells(card, template, 2)).Append('\n');
            }
            if (doc.IsNew) foreach (string locale in CharacterIdentityCatalog.Locales) {
                text.Append("\n[character.text.").Append(locale).Append("]\n");
                foreach (var field in new[] { ("name", 3, "chara_text", "NOUN_INFO", 5), ("short", 4, "chara_text", "NOUN_INFO", 5),
                    ("upper", 5, "chara_text", "NOUN_INFO", 5), ("description", 19, "chara_description_text", "TEXT_INFO", 2) }) {
                    string path = Path.Combine(data, "common", "text", locale, field.Item3 + ".cfg.bin");
                    if (!File.Exists(path)) continue;
                    if (!languageTables.TryGetValue(path, out var table)) languageTables.Add(path, table = Read(path));
                    var localized = table.Rows.FirstOrDefault(r => r.Name == field.Item4 && r.UInt(0) == row.UInt(field.Item2) && (r.Name != "NOUN_INFO" || r.Int(1) == 0));
                    if (localized != null) text.Append(field.Item1).Append(" = ").Append(Q(localized.Text(field.Item5))).Append('\n');
                }
            }
            WriteModel(data, root, resolvedIds[i], model.Text(10));
        }
        Write(root, "content/characters/characters.toml", text);
        WriteAlbumFreeRecruit(documents, resolvedIds, bases, parameters, root);
        WriteModeChangeFilters(resolvedIds, bases, parameters, data, root);
        File.Delete(basePath); File.Delete(modelPath); File.Delete(paramPath);
        WriteTexts(data, root);
        WriteShop(data, root);
        WriteVoiceRegistry(data, root);
        WriteAuras(data, root);
        WriteKeyed(data, root, "character", "chara_change_", "CHARA_MODE_CHANGE", "character/chara_mode_change", new[] { "from_param", "to_param" });
        WriteSkillItems(data, root);
    }

    static void WriteModeChangeFilters(string[] characterIds, CfgTable bases, CfgTable parameters, string data, string root)
    {
        var characters = new HashSet<uint>(Rows(bases, "CHARA_BASE_INFO")
            .Where(r => characterIds.Contains(r.Text(1))).Select(r => r.UInt(0)));
        var cards = Rows(parameters, "CHARA_PARAM_INFO").Where(r => characters.Contains(r.UInt(1))).ToArray();
        var skills = new HashSet<uint>(cards.SelectMany(r => Enumerable.Range(0, 9).Select(slot => r.UInt(11 + slot * 2))));
        string folder = Path.Combine(data, "common", "gamedata", "skill");
        string[] paths = Directory.Exists(folder) ? Directory.GetFiles(folder, "aura_skill_config*.cfg.bin") : Array.Empty<string>();
        if (paths.Length == 0) paths = Directory.GetFiles(Path.Combine(DumpDataManager.CommonPath, "gamedata", "skill"), "aura_skill_config*.cfg.bin");
        uint[] modes = paths.SelectMany(p => Rows(Read(p), "AURA_CMD_INFO"))
            .Where(r => r.Int(10) == 6 && skills.Contains(r.UInt(0))).Select(r => r.UInt(0)).Distinct().OrderBy(id => id).ToArray();
        if (modes.Length == 0) return;
        // A mode-change aura links to itself, with no ordinary SKILL_INFO row.
        // Fantasy's type/element picker otherwise rejects it before displaying
        // the row, just like its own wmc_evt carriers without filter_pass.
        Write(root, "content/transforms/character_mode_changes.toml", new StringBuilder("schema = \"VRFW.transform/2\"\n\n[ability_list]\nfilter_pass = [")
            .Append(string.Join(", ", modes.Select(Id))).Append("]\n"));
        // Character Reforged removes auras from ordinary technique slots.
        // Publish the mode through its public per-card Mix defaults as well,
        // so a mode originally learnt in slots 1-8 reaches the dedicated slot.
        var movesets = new StringBuilder("schema = \"CharacterReforged.movesets/1\"\n\n[CharacterReforged.Mix]\n");
        var picker = new StringBuilder("-- Generated: stock entries for this Mod's learnt mode changes.\nlocal modes = {\n");
        foreach (var card in cards.OrderBy(r => r.UInt(0))) {
            var selected = Enumerable.Range(0, 9).Select(slot => card.UInt(11 + slot * 2)).Where(modes.Contains).Distinct().ToArray();
            if (selected.Length == 0) continue;
            if (selected.Length > 1) throw new InvalidDataException("Multiple mode changes for Fantasy card: " + Id(card.UInt(0)));
            movesets.Append(Q(Id(card.UInt(0)))).Append(" = ").Append(Id(selected[0])).Append('\n');
            long level = Enumerable.Range(0, 9).Where(slot => card.UInt(11 + slot * 2) == selected[0]).Min(slot => card.Int(12 + slot * 2));
            picker.Append("  [").Append(Id(card.UInt(0))).Append("] = { skill = ").Append(Id(selected[0]))
                .Append(", level = ").Append(Math.Max(1, level)).Append(" },\n");
        }
        Write(root, "content/movesets/character_mode_changes.toml", movesets);
        picker.Append(@"}
local function u32(value)
  local n = tonumber(value)
  return n and n % 4294967296 or nil
end
local function prepare()
  if type(VRFW_Param) ~= 'table' or type(VRFW_Param.Get) ~= 'function' then return end
  -- evt_mx_base: the player handed to the shared Hiper picker.
  local hdl = u32(VRFW_Param.Get(19616700))
  if not hdl or hdl == 0 then return end
  local ok, param = pcall(funcLuaCommand, 1131797651, hdl, 4)
  if not ok then return end
  param = u32(param)
  local mode = modes[param]
  local okLevel, level = pcall(funcLuaCommand, 1131797651, hdl, 0)
  local have, after = nil, nil
  if mode and okLevel and type(level) == 'number' and level >= mode.level then
    local okCount, count = pcall(funcLuaCommand, @ITEM_NUM@, mode.skill)
    have = okCount and tonumber(count) or nil
    if have and have <= 0 then
      pcall(funcLuaCommand, @ADD_ITEM@, mode.skill, 1, false)
    end
    local okAfter, countAfter = pcall(funcLuaCommand, @ITEM_NUM@, mode.skill)
    after = okAfter and countAfter or nil
  end
  pcall(funcLuaCommand, 3661580131, string.format('CharMaker mode picker: player=%s param=%s level=%s mode=%s required=%s stock=%s after=%s',
    tostring(hdl), tostring(param), tostring(level), tostring(mode and mode.skill or 0),
    tostring(mode and mode.level or 0), tostring(have), tostring(after)))
  if mode then
    local okRule, restricted, allowed = pcall(funcLuaCommand, 3462128076, mode.skill, hdl)
    local slot = VRFW_Param.Get(4199810934)
    local okPlace, place, reason = pcall(funcLuaCommand, @CAN_PLACE@, hdl, slot or 8, mode.skill)
    local okInfo, found, category = pcall(funcLuaCommand, 2580586398, mode.skill, 9)
    local okType, typeFound, auraType = pcall(funcLuaCommand, 2580586398, mode.skill, 13)
    pcall(funcLuaCommand, 3661580131, string.format('CharMaker mode rules: native=%s restricted=%s allowed=%s slot=%s placeCall=%s place=%s reason=%s infoCall=%s found=%s category=%s typeFound=%s auraType=%s',
      tostring(okRule), tostring(restricted), tostring(allowed), tostring(slot), tostring(okPlace), tostring(place), tostring(reason),
      tostring(okInfo), tostring(found), tostring(category), tostring(typeFound), tostring(auraType)))
  end
end
local original = OnInit
if type(original) == 'function' then
  function OnInit(...)
    local ok, err = pcall(prepare)
    if not ok then pcall(funcLuaCommand, 3661580131, 'CharMaker mode picker error:', tostring(err)) end
    return original(...)
  end
end
").Replace("@ITEM_NUM@", G4Runtime.G4TextureParser.Crc32Utf8("CMND_GET_ITEM_NUM").ToString(CultureInfo.InvariantCulture))
                .Replace("@ADD_ITEM@", G4Runtime.G4TextureParser.Crc32Utf8("CMND_ADD_ITEM_ONLY").ToString(CultureInfo.InvariantCulture))
                .Replace("@CAN_PLACE@", G4Runtime.G4TextureParser.Crc32Utf8("CMND_CHARACTERREFORGED_CAN_PLACE").ToString(CultureInfo.InvariantCulture));
        Write(root, "assets/lua/ability_list_menu/990_charamaker_mode_stock.lua", picker);
    }

    // ClassicRecruitment's album does not consult character.recruit. Its Lua
    // service calculates a separate value/level/rarity price (including a base
    // fee even for album_values = 0). Wrap its exported album action to cover
    // labels and recruitment without intercepting another Mod's private commands.
    static void WriteAlbumFreeRecruit(CharacterMakerDocument[] documents, string[] ids, CfgTable bases, CfgTable parameters, string root)
    {
        var freeCharacters = new HashSet<uint>(documents.Select((doc, i) => new { doc, i }).Where(x => x.doc.freeSoulInShop)
            .Select(x => Rows(bases, "CHARA_BASE_INFO").Single(r => r.Text(1) == ids[x.i]).UInt(0)));
        uint[] cards = Rows(parameters, "CHARA_PARAM_INFO").Where(r => freeCharacters.Contains(r.UInt(1))).Select(r => r.UInt(0)).Distinct().OrderBy(id => id).ToArray();
        if (cards.Length == 0) return;
        var script = new StringBuilder("-- Generated: free album recruitment for this Mod's explicitly selected cards.\n")
            .Append("CharMakerFreeRecruit = CharMakerFreeRecruit or { cards = {} }\nlocal state = CharMakerFreeRecruit\n");
        foreach (uint card in cards) script.Append("state.cards[").Append(Id(card)).Append("] = true\n");
        script.Append(@"
-- Use BetterQoL's exported AlbumActions interface. Each Mod's game-Lua
-- command binding is scoped: replacing this Mod's funcLuaCommand does not
-- intercept the ClassicRecruitment action's private command binding.
local function log(...)
  pcall(funcLuaCommand, 3661580131, 'CharMaker free recruitment:', ...)
end
local function unsigned(value)
  local n = tonumber(value)
  return n and n % 4294967296 or nil
end
local function text(id, name, cost, balance)
  local ok, value = pcall(funcLuaCommand, 4074371074, id)
  if not ok or type(value) ~= 'string' then return tostring(name or '') end
  local formatted, result = pcall(string.format, value, tostring(name or ''), cost or 0, balance or 0)
  return formatted and result or value
end
local function message(value, ask)
  if type(VRFW_Question) == 'table' then
    if ask then return VRFW_Question.Ask(value) end
    VRFW_Question.Notice(value)
    return
  end
  funcLuaCommand(2289627655, value, 0, 0, 0, 0, 0, false)
  funcLuaCommand(2351835063, ask and 2 or 1, 0)
  coroutine.yield()
  if not ask then
    while funcLuaCommand(898725220) == true do coroutine.yield() end
    return
  end
  local result = -1
  while result == nil or result < 0 do
    result = funcLuaCommand(1523741473)
    coroutine.yield()
  end
  while funcLuaCommand(682363669) ~= true do coroutine.yield() end
  return result == 0
end
local actions = type(BetterQoL) == 'table' and BetterQoL.AlbumActions or nil
if type(actions) ~= 'table' then log('album actions unavailable'); return end
state.hooks = state.hooks or {}
for _, action in ipairs(actions) do
  if action.id == 'ClassicRecruitment.Recruit' then
    local installed = state.hooks[action]
    if not installed or action.Run ~= installed.run or action.RowExtras ~= installed.extras then
      local originalRun, originalExtras = action.Run, action.RowExtras
      action.RowExtras = function(rows)
        local result = type(originalExtras) == 'function' and originalExtras(rows) or {}
        if result == nil then return nil end
        local extras = {}
        for k, value in pairs(result) do extras[k] = value end
        local waived = 0
        for k, row in ipairs(rows) do
          if state.cards[unsigned(row.param)] then extras[k] = nil; waived = waived + 1 end
        end
        if waived > 0 and not state.rowsLogged then
          state.rowsLogged = true
          log('waived album rows', waived)
        end
        return extras
      end
      action.Run = function(row)
        if not state.cards[unsigned(row.param)] then return originalRun(row) end
        local R = VRFW_Recruit
        if type(R) ~= 'table' then log('recruit service unavailable'); return false end
        local name = tostring(row.name or '')
        local can, why = R.CanSign(row.param)
        if row.owned == true or why == 'owned' then message(text(2127430293, name)); return false end
        if not can then message(text(1165105549, name)); return false end
        if not message(text(2442356306, name, 0, 0), true) then return false end
        local level = type(R.TeamLevel) == 'function' and R.TeamLevel() or 1
        -- Use the same core service as ClassicRecruitment: capacity checks,
        -- exact card/rarity, normal add-character path and autosave; no price.
        local ok, err = R.Recruit{param = row.param, level = level, rarity = row.rarity}
        log('free album recruit', unsigned(row.param), ok == true, tostring(err))
        message(text(ok == true and 290976732 or err == 'bank_full' and 1165105549 or 1534386840, name))
        return ok == true
      end
      state.hooks[action] = {run = action.Run, extras = action.RowExtras}
      log('album action installed')
    end
  end
end
");
        string key = G4Runtime.G4TextureParser.Crc32(string.Join(",", cards.Select(Id))).ToString("X8");
        Write(root, "assets/lua/util_album_menu/990_charamaker_free_recruit_" + key + ".lua", script);
    }

    static void AppendCells(StringBuilder text, CfgTable.Row row, CfgTable.Row original)
    {
        for (int n = 1; n < row.Values.Length; n++)
            if (original == null || n >= original.Values.Length || !Equals(row.Values[n], original.Values[n]))
                text.Append(ModelFields[n]).Append(" = ").Append(Value(row.Values[n])).Append('\n');
    }

    static void WriteModel(string data, string root, string id, string meshPath)
    {
        if (string.IsNullOrEmpty(meshPath) || !meshPath.Replace('\\', '/').StartsWith("_face/", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported Fantasy character head path.");
        string relative = "chr/" + Path.GetDirectoryName(meshPath).Replace('\\', '/');
        foreach (string platform in new[] { "common", "dx11" }) {
            string source = Path.Combine(data, platform, relative);
            if (!Directory.Exists(source)) continue;
            string destination = Path.Combine(root, "assets/models/characters", id, platform);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Move(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        Write(root, "content/models/characters/" + id + ".toml", new StringBuilder("schema = \"VRFW.model/1\"\nformat = \"native\"\ngame_path = ").Append(Q(relative)).Append('\n'));
    }

    // Keep the generated text IDs (also used by custom transformations) and emit
    // only changed rows. No complete language file can hide another mod's text.
    static void WriteTexts(string data, string root)
    {
        string directory = Path.Combine(data, "common", "text");
        if (!Directory.Exists(directory)) return;
        foreach (var group in Directory.GetFiles(directory, "*.cfg.bin", SearchOption.AllDirectories).GroupBy(Path.GetFileName)) {
            var changes = new SortedDictionary<string, (uint Id, string Kind, Dictionary<string, string> Locales)>();
            foreach (string path in group) {
                var output = Read(path); var native = Native(data, path);
                var originals = native.Rows.Where(r => r.Name == "NOUN_INFO" || r.Name == "TEXT_INFO").GroupBy(TextKey).ToDictionary(g => g.Key, g => g.First());
                string locale = Path.GetFileName(Path.GetDirectoryName(path));
                foreach (var row in output.Rows.Where(r => r.Name == "TEXT_INFO" || r.Name == "NOUN_INFO" && r.Int(1) == 0)) {
                    string key = TextKey(row); originals.TryGetValue(key, out var previous);
                    if (Same(row, previous)) continue;
                    if (!changes.TryGetValue(key, out var change)) change = (row.UInt(0), row.Name == "NOUN_INFO" ? "noun" : "text", new Dictionary<string, string>());
                    change.Locales[locale] = row.Text(row.Name == "NOUN_INFO" ? 5 : 2) ?? ""; changes[key] = change;
                }
            }
            if (changes.Count > 0) {
                var text = new StringBuilder("schema = \"VRFW.text/2\"\ntable = ").Append(Q(group.Key.Replace(".cfg.bin", ""))).Append('\n');
                foreach (var change in changes.Values) {
                    text.Append("\n[[row]]\nid = ").Append(Id(change.Id)).Append("\nkind = ").Append(Q(change.Kind)).Append('\n');
                    foreach (var language in change.Locales) text.Append(language.Key).Append(" = ").Append(Q(language.Value)).Append('\n');
                }
                Write(root, "text/rows/" + group.Key.Replace(".cfg.bin", ".toml"), text);
            }
            foreach (string path in group) File.Delete(path);
        }
    }
    static string TextKey(CfgTable.Row r) => r.Name + "/" + r.UInt(0) + (r.Name == "NOUN_INFO" ? "/" + r.Int(1) : "");

    static void WriteShop(string data, string root)
    {
        string path = Find(data, "shop", "shop_config_"); var output = Read(path); var native = Native(data, path);
        var text = new StringBuilder("schema = \"VRFW.base_table/1\"\n");
        var nativeShops = Rows(native, "SHOP_INFO");
        foreach (var shop in Rows(output, "SHOP_INFO")) {
            int shopIndex = Array.FindIndex(nativeShops, r => r.UInt(0) == shop.UInt(0));
            if (shopIndex < 0) throw new InvalidDataException("Fantasy character export cannot add a retail shop.");
            var nativeItems = Children(native, nativeShops[shopIndex], "SHOP_INFO_ITEM").GroupBy(r => r.UInt(0)).ToDictionary(g => g.Key, g => g.First());
            var items = Children(output, shop, "SHOP_INFO_ITEM");
            var added = items.Where(r => !nativeItems.ContainsKey(r.UInt(0))).ToArray();
            // Parent indexes refer only to original shops; append positions do not
            // depend on the count of rows added by other mods.
            if (added.Length > 0) {
                int count = Children(native, nativeShops[shopIndex], "SHOP_INFO_ITEM").Length;
                text.Append("\n[[list]]\nname = \"SHOP_INFO_ITEM\"\nparent = [[\"SHOP_INFO\", ").Append(shopIndex).Append("]]\nsplice = [{ at = ").Append(count).Append(", remove = 0, rows = [\n");
                foreach (var item in added) text.Append("  ").Append(ShopRow(output, item)).Append(",\n");
                text.Append("] }]\n");
            }
            foreach (var item in items.Where(r => nativeItems.TryGetValue(r.UInt(0), out var old) && (!Same(r, old) || ShopRow(output, r) != ShopRow(native, old)))) {
                int index = Array.FindIndex(Children(native, nativeShops[shopIndex], "SHOP_INFO_ITEM"), r => r.UInt(0) == item.UInt(0));
                text.Append("\n[[list]]\nname = \"SHOP_INFO_ITEM\"\nparent = [[\"SHOP_INFO\", ").Append(shopIndex)
                    .Append("]]\nsplice = [{ at = ").Append(index).Append(", remove = 1, rows = [").Append(ShopRow(output, item)).Append("] }]\n");
            }
        }
        Write(root, "content/base_tables/gamedata/shop/shop_config.toml", text); File.Delete(path);
    }
    static CfgTable.Row[] Children(CfgTable table, CfgTable.Row row, string name)
    {
        int start = table.Rows.IndexOf(row) + 1;
        return table.Rows.Skip(start).TakeWhile(r => r.Name != row.Name && r.Name != row.Name + "_LIST_END").Where(r => r.Name == name).ToArray();
    }
    static string ShopRow(CfgTable table, CfgTable.Row item) => "[" + string.Join(", ", item.Values.Select(Value)) +
        ", { SHOP_INFO_ITEM_CONSUME = [" + string.Join(", ", Children(table, item, "SHOP_INFO_ITEM_CONSUME").Select(r => "[" + string.Join(", ", r.Values.Select(Value)) + "]")) + "] }" +
        ", { SHOP_INFO_ITEM_CONDITION = [" + string.Join(", ", Children(table, item, "SHOP_INFO_ITEM_CONDITION").Select(r => "[" + string.Join(", ", r.Values.Select(Value)) + "]")) + "] }]";

    static void WriteVoiceRegistry(string data, string root)
    {
        var banks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(Path.Combine(data, "common", "gamedata", "character"), "general_battle_voice_*.cfg.bin")) {
            var output = new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(path), path);
            var native = new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "gamedata", "character", Path.GetFileName(path))), path);
            var list = output.GetList("m_generalBattleVoice"); var original = native.GetList("m_generalBattleVoice");
            var ids = new HashSet<uint>(Enumerable.Range(0, original.Count).Select(i => native.U32(original.Row(i) + original.Field("voiceId"))));
            var text = new StringBuilder("schema = \"VRFW.base_table/1\"\n\n[[keyed]]\ntable = \"character/general_battle_voice\"\n");
            for (int i = 0; i < list.Count; i++) {
                int row = list.Row(i); uint id = output.U32(row + list.Field("voiceId")); if (ids.Contains(id)) continue;
                string bank = output.String(row + list.Field("voiceIdString"));
                banks.Add(bank);
                text.Append("\n[[keyed.add]]\nkey = ").Append(Id(id)).Append("\ncells = { voice_id_string = ").Append(Q(bank)).Append(" }\n");
            }
            Write(root, "content/base_tables/gamedata/character/general_battle_voice.toml", text); File.Delete(path);
        }
        if (banks.Count == 0) return;
        // Unlike the retail CPK registry, the generic character-voice table does
        // not register new cue sheets with Fantasy's audio engine. Mark these
        // as localized voice banks so CRI opens sound_asset/<language>/<bank>,
        // rather than trying to open a nonlocalized ACB at sound_asset/<bank>.
        var sounds = new StringBuilder("schema = \"VRFW.sound/1\"\n");
        foreach (string bank in banks) {
            bool found = false;
            foreach (string language in new[] { "ja", "en" }) {
                string acb = Path.Combine(data, "common", "sound_asset", language, bank + ".acb");
                string awb = Path.ChangeExtension(acb, ".awb");
                if (!File.Exists(acb) && !File.Exists(awb)) continue;
                if (!File.Exists(acb) || !File.Exists(awb)) throw new InvalidDataException("Incomplete Fantasy voice bank: " + language + "/" + bank);
                EncodeVoiceFile(acb, "@UTF");
                EncodeVoiceFile(awb, "AFS2");
                found = true;
            }
            if (!found) throw new InvalidDataException("Missing Fantasy voice bank: " + bank);
            sounds.Append("\n[[bank]]\nname = ").Append(Q(bank)).Append("\nvoice = true\n");
        }
        Write(root, "content/sounds/character_voices.toml", sounds);
    }

    // The retail export is plaintext: Viola encrypts audio while packing the
    // CPK. Fantasy serves loose assets directly, so that packing step never
    // runs. The game still applies its filename-key XOR before passing the
    // payload to CRI; both ACB and AWB must therefore be encoded here.
    static void EncodeVoiceFile(string path, string magic)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || Encoding.ASCII.GetString(bytes, 0, 4) != magic)
            throw new InvalidDataException("Expected plaintext Fantasy audio: " + path);
        uint key = G4Runtime.G4TextureParser.Crc32Utf8(Path.GetFileName(path));
        var crcTable = new uint[256];
        for (uint i = 0; i < crcTable.Length; i++) {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            crcTable[i] = crc;
        }
        for (int offset = 0; offset < bytes.Length; offset += 4) {
            uint crc = ~(uint)offset;
            for (int k = 0; k < 4; k++) crc = (crc >> 8) ^ crcTable[(crc ^ (key >> (k * 8))) & 255];
            crc = ~crc;
            for (int i = 0; i < 4 && offset + i < bytes.Length; i++) {
                int shift = i * 2;
                uint mask = ((crc >> (shift + 8)) & 3) | (((crc >> shift) & 255) << 2 & 255);
                mask = ((mask << 2) & 255) | ((crc >> (shift + 16)) & 3);
                mask = ((mask << 2) & 255) | ((crc >> (shift + 24)) & 3);
                bytes[offset + i] ^= (byte)mask;
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    static void WriteKeyed(string data, string root, string folder, string prefix, string rowName, string tableName, string[] fields)
    {
        string directory = Path.Combine(data, "common", "gamedata", folder);
        if (!Directory.Exists(directory)) return;
        string[] paths = Directory.GetFiles(directory, prefix + "*.cfg.bin");
        if (paths.Length == 0) return;
        var declarations = new SortedDictionary<uint, string>();
        foreach (string path in paths) {
            var output = Read(path); var native = Native(data, path);
            var originals = Rows(native, rowName).ToDictionary(r => r.UInt(0));
            foreach (var row in Rows(output, rowName)) {
                originals.TryGetValue(row.UInt(0), out var old); if (Same(row, old)) continue;
                var declaration = new StringBuilder(old == null ? "\n[[keyed.add]]\n" : "\n[[keyed.set]]\n").Append("key = ").Append(Id(row.UInt(0)))
                    .Append("\ncells = { ").Append(string.Join(", ", Enumerable.Range(1, row.Values.Length - 1).Where(i => old == null || !Equals(row.Values[i], old.Values[i])).Select(i => fields[i] + " = " + Value(row.Values[i])))).Append(" }\n");
                if (declarations.TryGetValue(row.UInt(0), out var previous) && previous != declaration.ToString())
                    throw new InvalidDataException("Conflicting Fantasy table versions: " + tableName + "/" + Id(row.UInt(0)));
                declarations[row.UInt(0)] = declaration.ToString();
            }
        }
        string relative = "content/base_tables/gamedata/" + folder + "/" + prefix.TrimEnd('_') + ".toml";
        string existing = Path.Combine(root, relative);
        var text = new StringBuilder(File.Exists(existing) ? File.ReadAllText(existing) : "schema = \"VRFW.base_table/1\"\n");
        if (declarations.Count > 0) {
            text.Append("\n[[keyed]]\ntable = ").Append(Q(tableName)).Append('\n');
            foreach (string declaration in declarations.Values) text.Append(declaration);
            Write(root, relative, text);
        }
        foreach (string path in paths) File.Delete(path);
    }

    static void WriteAuras(string data, string root)
    {
        string directory = Path.Combine(data, "common", "gamedata", "skill"); if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.GetFiles(directory, "aura_skill_config*.cfg.bin")) {
            var output = Read(path); var native = Native(data, path); var originals = Rows(native, "AURA_CMD_INFO").ToDictionary(r => r.UInt(0));
            var text = new StringBuilder("schema = \"VRFW.technique/3\"\n");
            foreach (var row in Rows(output, "AURA_CMD_INFO")) {
                originals.TryGetValue(row.UInt(0), out var old);
                var companions = output.Rows.Skip(output.Rows.IndexOf(row) + 1).TakeWhile(r => r.Name.StartsWith("AURA_CMD_INFO_REF_", StringComparison.Ordinal)).ToArray();
                bool changed = !Same(row, old);
                foreach (var reference in companions) {
                    string child = reference.Name.Replace("AURA_CMD_INFO_REF_", "AURA_CMD_");
                    var a = Referenced(output, reference, child); var b = old == null ? Array.Empty<CfgTable.Row>() : Referenced(native, native.Rows.Skip(native.Rows.IndexOf(old) + 1).First(r => r.Name == reference.Name), child);
                    changed |= a.Length != b.Length || !a.Zip(b, Same).All(v => v);
                }
                if (!changed) continue;
                var template = old ?? Rows(native, "AURA_CMD_INFO").First(r => r.Int(10) == row.Int(10));
                if (old == null) text.Append("\n[[aura_command]]\nkey = ").Append(Q(row.Text(1))).Append("\nid = ").Append(Id(row.UInt(0)))
                    .Append("\nfrom = ").Append(Q(template.Text(1))).Append('\n');
                else text.Append("\n[[aura]]\naura = ").Append(Q(row.Text(1))).Append('\n');
                text.Append("cells = { ").Append(string.Join(", ", Enumerable.Range(2, row.Values.Length - 2)
                    .Where(n => !Equals(row.Values[n], template.Values[n])).Select(n => AuraFields[n] + " = " + Value(row.Values[n])))).Append(" }\n");
                foreach (var reference in companions) {
                    string suffix = reference.Name.Substring("AURA_CMD_INFO_REF_".Length);
                    string field = suffix == "CHARA" ? "charas" : suffix == "EFFECT" ? "effects" : "unique_effects";
                    text.Append(field).Append(" = [").Append(string.Join(", ", Referenced(output, reference, "AURA_CMD_" + suffix).Select(r => "[" + string.Join(", ", r.Values.Select(Value)) + "]"))).Append("]\n");
                }
            }
            Write(root, "content/techniques/character_auras.toml", text); File.Delete(path);
        }
    }
    static CfgTable.Row[] Referenced(CfgTable table, CfgTable.Row reference, string child)
    {
        int start = checked((int)reference.Int(0)), count = checked((int)reference.Int(1));
        if (count == 0) return Array.Empty<CfgTable.Row>();
        var rows = Rows(table, child);
        if (start < 0 || count < 0 || start > rows.Length - count) throw new InvalidDataException("Invalid Fantasy companion reference.");
        return rows.Skip(start).Take(count).ToArray();
    }
    static void WriteSkillItems(string data, string root) => WriteKeyed(data, root, "item", "item_config_", "ITEM_SPECIAL_SKILL_INFO", "item/item_special_skill",
        new[] { "id", "reserved_1", "name_text", "desc_text", "catalog_no", "category", "skill_kind", "max_stack", "reserved_8", "reserved_9", "reserved_10", "key", "rarity", "reserved_13", "base_item", "icon_index", "linked", "flag_17" });
}
