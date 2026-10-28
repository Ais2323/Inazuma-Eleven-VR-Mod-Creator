This project partially utilizes AI.

If I have spare time after get off work, I will refine the AI's code.

https://github.com/The-RealBobi/Viola

This tool does not provide game assets. You need to dump the game data from Inazuma Eleven: Victory Road using the Viola tool above. Select the dumped game assets to use.

https://github.com/Adr1GR/IEVR_Mod_Manager
The exported mods can be installed and executed through IEVR Mod Manager.

The Viola.CLI-Portable.exe part can be done using The-RealBobi Viola v2.0.0.

Thanks to The-RealBobi and SuperTavorzura for providing the mod tools.


## Portable settings and translations

Desktop players read `setting.json` and the `Localization/` directory next to the executable (outside the `.app` bundle on macOS). They do not read or migrate files from the old persistent-data location.

The build postprocessor copies language JSON files to `Localization/` beside the player. `Assets/Localization` takes precedence over bundled `Assets/Resources/Localization` when both contain the same language. Existing output translations are preserved; only missing files are copied. To replace an older output translation with the project version, remove that output JSON before rebuilding.

`setting.json` is created with defaults on first launch. The developer's `Assets/setting.json` is never copied into a build. The Unity Editor continues to use `Assets/setting.json` and `Assets/Localization`.
