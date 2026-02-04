# directory.md

## 1 : Windows Directory Structure (Repo)

D:\Aetherforge\
├─ .editorconfig
├─ .gitattributes
├─ .gitignore
├─ Aetherforge.sln
├─ Directory.Build.props
├─ global.json
├─ .github\
│  └─ workflows\
├─ config\
│  ├─ pinned.yaml
│  ├─ settings.yaml
│  └─ profiles\
│     ├─ agent.yaml
│     ├─ coding.yaml
│     └─ general.yaml
├─ docs\
│  ├─ directory.md
│  ├─ spec.md
│  ├─ roadmap.md
│  ├─ AetherChecklist.M0.md
│  ├─ AetherChecklist.M1.md
│  ├─ AetherChecklist.M2.md
│  ├─ AetherChecklist.M3.md
│  ├─ AetherChecklist.M4.md
│  ├─ AetherChecklist.M5.md
│  └─ AetherChecklist.M6.md
├─ exports\
├─ logs\
│  └─ bootstrap\
│     ├─ gpu_inference_evidence.txt
│     └─ status.json
├─ scripts\
│  ├─ aether.ps1
│  └─ commands\
│     ├─ ask.ps1
│     ├─ dev-core.ps1
│     ├─ doctor.ps1
│     ├─ export.ps1
│     ├─ rebuild.ps1
│     ├─ restore.ps1
│     ├─ start.ps1
│     ├─ status.ps1
│     └─ test.ps1
├─ src\
│  ├─ Aetherforge.Contracts\
│  │  ├─ Aetherforge.Contracts.csproj
│  │  └─ Class1.cs
│  ├─ Aetherforge.Core\
│  │  ├─ Aetherforge.Core.csproj
│  │  ├─ Aetherforge.Core.csproj.user
│  │  ├─ Program.cs
│  │  ├─ appsettings.json
│  │  ├─ appsettings.Development.json
│  │  └─ Properties\launchSettings.json
│  ├─ Aetherforge.CoreClient\
│  │  ├─ Aetherforge.CoreClient.csproj
│  │  └─ Class1.cs
│  ├─ Aetherforge.Windows.Tests\
│  │  ├─ Aetherforge.Windows.Tests.csproj
│  │  └─ UnitTest1.cs
│  └─ Aetherforge.Windows.Ui\
│     ├─ Aetherforge.Windows.Ui.csproj
│     ├─ App.xaml
│     ├─ App.xaml.cs
│     ├─ AssemblyInfo.cs
│     ├─ MainWindow.xaml
│     └─ MainWindow.xaml.cs
└─ wsl\
   └─ core\
      └─ scripts\
         ├─ run-dev.sh
         └─ smoke.sh


## 2 : WSL-native state (durable runtime data; NOT in repo)

/
└── var/
    └── lib/
        ├── aetherforge/
        │   ├── conversations.sqlite
        │   ├── conversations.sqlite-wal        (expected when WAL active + writes occur)
        │   └── conversations.sqlite-shm        (expected when WAL active + writes occur)
        └── ollama/
            └── <model blobs + manifests>       (models_dir reported by status)
