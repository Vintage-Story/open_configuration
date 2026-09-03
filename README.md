# Open Configuration

API compartilhada para mods do Vintage Story lidarem com arquivos de configuração (`ModConfig/.../*.json`)
sem repetir o bloco de código de carregamento em cada mod.

Hoje, cada mod (`RPGDifficulty`, `ServerEssentials`, `AFKModule`, etc.) tem seu próprio `Configuration.cs`
com um método `LoadConfigurationByDirectoryAndName` copiado e, para cada campo, um bloco:

```csharp
if (baseConfigs.TryGetValue("enableWhitelist", out object value))
    if (value is null) Debug.LogError("CONFIGURATION ERROR: enableWhitelist is null");
    else if (value is not bool) Debug.LogError($"CONFIGURATION ERROR: enableWhitelist is not boolean is {value.GetType()}");
    else enableWhitelist = (bool)value;
else Debug.LogError("CONFIGURATION ERROR: enableWhitelist not set");
```

O Open Configuration elimina esse bloco por completo: você declara uma classe com os campos e seus valores
default (do jeito que já é feito hoje) e chama um único método para carregar, validar, preencher chaves
faltantes e persistir o arquivo.

## Instalação em outro mod

1. Referencie a DLL compilada no `.csproj` do mod consumidor:

```xml
<ItemGroup>
  <Reference Include="OpenConfiguration">
    <HintPath>../open_configuration/OpenConfiguration/bin/$(Configuration)/Mods/mod/OpenConfiguration.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

2. Declare a dependência no `modinfo.json` do mod consumidor:

```json
"dependencies": {
    "game": "1.21.0",
    "openconfiguration": ""
}
```

## Uso básico

Defina uma classe simples com os campos e seus defaults (nada de `static`, nada de dicionário):

```csharp
public class BaseConfig
{
    public bool enableWhitelist = false;
    public bool enableBlacklist = true;
    public int increaseStatsEveryDownHeight = 10;
    public double lifeStatsIncreaseEveryHeight = 0.1;
    public List<string> homeSyntaxes = ["home"];
    public Dictionary<string, double> whitelistDistance = [];
}
```

Carregue durante `AssetsLoaded` (ou onde o mod já chamava `UpdateBaseConfigurations`):

```csharp
using OpenConfiguration;

public static class Configuration
{
    public static ModLogger Logger;
    public static BaseConfig Base = new();

    public static void Load(ICoreAPI api)
    {
        Logger ??= new ModLogger(api.Logger, "RPGDifficulty");
        Base = ConfigManager.LoadModConfig<BaseConfig>(api, "RPGDifficulty", "base", Logger);
    }
}
```

E use normalmente: `Configuration.Base.enableWhitelist`, `Configuration.Base.homeSyntaxes`, etc.

Para salvar de volta (ex: depois de uma alteração em runtime):

```csharp
ConfigManager.SaveModConfig(api, "RPGDifficulty", "base", Configuration.Base, Configuration.Logger);
```

## O que a API resolve por você

- **Cria o diretório e o arquivo na primeira execução**, usando os valores default da classe (ou, opcionalmente,
  um asset do mod via o parâmetro `defaultAsset`, para quem prefere manter defaults em `assets/.../config/*.json`).
- **Preenche chaves que faltam no arquivo do jogador** (ex: mod atualizou e adicionou um campo novo) com o valor
  default e reescreve o arquivo, logando um aviso — sem apagar o que o jogador já tinha configurado.
- **Detecta campo com tipo incompatível** (ex: `"enableWhitelist": "sim"` em vez de `true`), loga o erro
  especificando a chave e mantém o valor default para aquele campo, sem derrubar o carregamento do resto do arquivo.
- **Não perde dados em caso de JSON corrompido**: se o arquivo inteiro não puder ser interpretado, ele é copiado
  para `nome.broken-AAAAMMDDHHmmss.json` antes de recriar o arquivo com os defaults, então nada é descartado
  silenciosamente.
- **Listas e dicionários são substituídos, não somados** ao valor default (cuidado comum ao usar
  `JsonConvert.PopulateObject` sem essa configuração: os itens do arquivo seriam anexados aos defaults).

## Logging (`ModLogger`)

Substitui a classe `Debug` que hoje é copiada em `Initialization.cs` de cada mod:

```csharp
var logger = new ModLogger(api.Logger, "RPGDifficulty");
logger.Log("mensagem normal");
logger.LogWarn("aviso");
logger.LogError("erro");
logger.LogDebug("só aparece se ExtendedLoggingEnabled = true");
```

`ModLogger` é uma instância (não estático): cada mod cria a sua, já que a mesma DLL do Open Configuration é
carregada uma única vez no processo e compartilhada entre todos os mods que dependem dela.

## API completa

Em `OpenConfiguration.ConfigManager`:

- `T Load<T>(ICoreAPI api, string relativeDirectory, string configName, ModLogger? logger = null, string? defaultAsset = null)`
- `T LoadModConfig<T>(ICoreAPI api, string modFolderName, string configName, ModLogger? logger = null, string? defaultAsset = null)`
  — atalho para `relativeDirectory = "ModConfig/{modFolderName}/config"`.
- `void Save<T>(ICoreAPI api, string relativeDirectory, string configName, T config, ModLogger? logger = null)`
- `void SaveModConfig<T>(ICoreAPI api, string modFolderName, string configName, T config, ModLogger? logger = null)`

`T` precisa ser uma classe com construtor sem parâmetros (`class, new()`); os valores default são os que você
atribuir aos campos/propriedades na declaração.

## Migrando um mod existente

Não é necessário migrar tudo de uma vez. Passo a passo sugerido por mod:

1. Copie os campos do `Configuration.cs` atual para uma (ou mais, uma por arquivo de config) classe POCO nova,
   mantendo os mesmos nomes e valores default.
2. Troque o corpo de `UpdateBaseConfigurations` por uma chamada a `ConfigManager.LoadModConfig<...>`.
3. Apague o método `LoadConfigurationByDirectoryAndName` e todos os blocos `TryGetValue`/cast.
4. Atualize os usos de `Configuration.campo` para `Configuration.Base.campo` (ou o nome que der ao holder).
5. Repita para cada arquivo de config do mod (translations, whitelist, etc.), cada um como sua própria classe.

## Building

Mesmo fluxo dos outros mods deste workspace: `./build.ps1` ou `./build.sh` (usa o `CakeBuild`).
