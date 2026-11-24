# Kitsune Sophon Downloader

An advanced tool for downloading game assets through the Sophon-based content delivery system.  
Downloads assets only, not the game launcher.


## Requirements

- Windows x64
- Install <i>[.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)</i>


## Compile Instructions

To compile the project:
1. Run `compile.bat`
2. The release output will be generated automatically inside the `bin` folder


## Sophon Content Labels

Chinese category labels returned by the Sophon API are automatically translated into English for display in the CONTENT list and download queue. Translation is display-only: the original API category name, category ID, matching field, manifest name, and download data are left unchanged. Successful translations are cached in the existing `sophon.db`. The translator uses Google Translate's public web endpoint without an API key; that endpoint is unofficial and may be rate-limited or changed. When translation is unavailable, Kitsune Sophon Downloader continues with the original API label.


## Sophon Download Folder Layout

The selected **Download Root** is used as the parent directory. Every selected Sophon content item gets its own folder, including when several content items are selected together. Folder names retain the content prefix from `category_id` and the language/content value from `matching_field` (without changing the value sent to the Sophon API), together with the version values, for example:

```
SophonDownloads/
└── Genshin Impact/
    ├── game_7.2.0_ldiff/
    │   ├── ldiff/
    │   └── <manifest>
    ├── audio_en-us_7.2.0_ldiff/
    │   ├── ldiff/
    │   └── <manifest>
    ├── game_7.1.0_7.2.0_ldiff/
    │   ├── ldiff/
    │   └── <manifest>
    └── audio_en-us_7.1.0_7.2.0_ldiff/
        ├── ldiff/
        └── <manifest>
```

## Disclaimer

This project is an independent open-source tool.  
It is not affiliated with any game developer, publisher, or official launcher.  
This software is intended for educational, research, archival, testing, and personal asset management purposes only.
