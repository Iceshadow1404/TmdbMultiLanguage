# TmdbMultiLanguage 

This Jellyfin plugin fetches movie and TV show images from TMDB with configurable multi-language support. It allows you to prioritize images in specific languages for your media library.

# WIP, please feel free to report any bugs.

## 📦 Install Instructions (Jellyfin)

1. Open **Jellyfin → Dashboard → Plugins → Repositories**
2. Click **Add Repository**
3. Enter the following URL: `https://raw.githubusercontent.com/Iceshadow1404/TmdbMultiLanguage/master/manifest.json`
4. Save, then go to **Plugins → Catalog** and install the plugin.
5. Restart Jellyfin.

## 🛠️ Build Instructions

To build the project, follow these steps:

1.  **Restore dependencies:**
    ```bash
    dotnet restore
    ```
    *(This command fetches all necessary packages for your project.)*
2.  **Build the project:**
    ```bash
    dotnet build --configuration Release
    ```
