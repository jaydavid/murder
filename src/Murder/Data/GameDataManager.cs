﻿using Bang.Systems;
using Murder.Assets;
using Murder.Assets.Graphics;
using Murder.Assets.Localization;
using Murder.Core;
using Murder.Core.Graphics;
using Murder.Diagnostics;
using Murder.Serialization;
using Murder.Services;
using Murder.Utilities;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Effect = Microsoft.Xna.Framework.Graphics.Effect;
using Texture2D = Microsoft.Xna.Framework.Graphics.Texture2D;

namespace Murder.Data
{
    public partial class GameDataManager : IDisposable
    {
        protected enum ShaderStyle
        {
            Dither,
            Posterize,
        }

        public const char SKIP_CHAR = '_';

        public const string HiddenAssetsRelativePath = "_Hidden";

        /// <summary>
        /// Maps:
        /// [Game asset type] -> [Guid] 
        /// </summary>
        protected readonly Dictionary<Type, HashSet<Guid>> _database = new();

        /// <summary>
        /// Maps:
        /// [Guid] -> [Asset]
        /// </summary>
        protected readonly Dictionary<Guid, GameAsset> _allAssets = new();

        public readonly CacheDictionary<string, Texture2D> CachedUniqueTextures = new(32);

        public ImmutableDictionary<int, PixelFont> _fonts = ImmutableDictionary<int, PixelFont>.Empty;

        private readonly HashSet<AtlasId> _referencedAtlases = [];

        /// <summary>
        /// The cheapest and simplest shader.
        /// </summary>
        public Effect? ShaderSimple = null;

        /// <summary>
        /// Actually a fancy shader, has some sprite effect tools for us, like different color blending modes.
        /// </summary>
        public Effect? ShaderSprite = null;

        /// <summary>
        /// A shader specialized for rendering pixel art.
        /// </summary>
        public Effect? ShaderPixel = null;

        /// <summary>
        /// Custom optional game shaders, provided by <see cref="_game"/>.
        /// </summary>
        public Effect?[] CustomGameShaders = [];

        /// <summary>
        /// Current localization data.
        /// </summary>
        public LanguageIdData CurrentLocalization { get; private set; } = Languages.English;

        public virtual Effect[] OtherEffects { get; } = Array.Empty<Effect>();

        public readonly Dictionary<AtlasId, TextureAtlas> LoadedAtlasses = new();

        public Texture2D DitherTexture = null!;

        protected GameProfile? _gameProfile;

        protected string? _assetsBinDirectoryPath;

        public string AssetsBinDirectoryPath => _assetsBinDirectoryPath!;

        private string? _packedBinDirectoryPath;
        public string PackedBinDirectoryPath => _packedBinDirectoryPath!;

        public string BinResourcesDirectoryPath => _binResourcesDirectory!;
        
        public GameProfile GameProfile
        {
            get
            {
                GameLogger.Verify(_gameProfile is not null, "Why are we acquiring game settings without calling Init() first?");
                return _gameProfile;
            }
            protected set => _gameProfile = value;
        }

        public JsonSerializerOptions SerializationOptions => _game?.Options ?? MurderSerializerOptionsExtensions.Options;

        protected virtual GameProfile CreateGameProfile() => _game?.CreateGameProfile() ?? new();

        public const string GameProfileFileName = @"game_config";

        protected readonly string ShaderRelativePath = Path.Join("shaders", "{0}.fxb");

        protected string _binResourcesDirectory = "resources";

        protected readonly IMurderGame? _game;

        /// <summary>
        /// Used for loading the editor asynchronously.
        /// </summary>
        public object AssetsLock = new();

        /// <summary>
        /// Whether we should call the methods after an async load has happened.
        /// </summary>
        public volatile bool CallAfterLoadContent = false;

        public Task LoadContentProgress = Task.CompletedTask;

        /// <summary>
        /// Perf: In order to avoid reloading assets that were generated by the importers, track which ones
        /// were reloaded completely here.
        /// </summary>
        protected readonly HashSet<string> _skipLoadingAssetAtPaths = new();

        /// <summary>
        /// Whether there was an error on <see cref="TryLoadAsset(string, string, bool, bool)"/>.
        /// </summary>
        private volatile bool _errorLoadingLastAsset = false;

        /// <summary>
        /// Whether we will continue trying to deserialize a file after finding an issue.
        /// </summary>
        public virtual bool IgnoreSerializationErrors => false;

        public readonly FileManager FileManager;

        /// <summary>
        /// Creates a new game data manager.
        /// </summary>
        /// <param name="game">This is set when overriding Murder utilities.</param>
        public GameDataManager(IMurderGame? game) : this(game, new FileManager()) { }

        /// <summary>
        /// Creates a new game data manager.
        /// </summary>
        /// <param name="game">This is set when overriding Murder utilities.</param>
        /// <param name="fileManager">File manager for the game.</param>
        protected GameDataManager(IMurderGame? game, FileManager fileManager)
        {
            _game = game;
            FileManager = fileManager;
        }

        public LocalizationAsset Localization => GetLocalization(CurrentLocalization.Id);

        public LocalizationAsset GetDefaultLocalization() => GetLocalization(LanguageId.English);

        protected virtual LocalizationAsset GetLocalization(LanguageId id)
        {
            if (!Game.Profile.LocalizationResources.TryGetValue(id, out Guid resourceGuid))
            {
                GameLogger.Warning($"Unable to get resource for {id}");

                // Try to fallback to english...?
                if (!Game.Profile.LocalizationResources.TryGetValue(LanguageId.English, out resourceGuid))
                {
                    throw new ArgumentException("No localization resources available.");
                }
            }

            return Game.Data.GetAsset<LocalizationAsset>(resourceGuid);
        }

        public void ChangeLanguage(LanguageId id) => ChangeLanguage(Languages.Get(id));

        public void ChangeLanguage(LanguageIdData data)
        {
            Game.Preferences.SetLanguage(data.Id);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(data.Identifier);

            CurrentLocalization = data;
        }

        [MemberNotNull(
            nameof(_binResourcesDirectory),
            nameof(_assetsBinDirectoryPath),
            nameof(_packedBinDirectoryPath))]
        public virtual void Initialize(string resourcesBinPath = "resources")
        {
            _database.Clear();
            _allAssets.Clear();

            _binResourcesDirectory = resourcesBinPath;

            LoadGameSettings();

            _assetsBinDirectoryPath = FileHelper.GetPath(_binResourcesDirectory, GameProfile.AssetResourcesPath);
            _packedBinDirectoryPath = FileHelper.GetPath(_binResourcesDirectory);
        }

        public void ClearContent()
        {
            foreach (var texture in CachedUniqueTextures)
            {
                texture.Value.Dispose();
            }

            CachedUniqueTextures.Clear();
            _fonts = _fonts.Clear();
        }

        public virtual void LoadContent()
        {
            // Clear asset dictionaries for the new assets
            _database.Clear();

            InitShaders();
            PreloadContent();

            // These will use the atlas as part of the deserialization.
            LoadContentProgress = Task.Run(LoadContentAsync);
        }

        protected void PreloadContent()
        {
            PreloadContentImpl();
            OnAfterPreloadLoaded();
        }

        protected async Task LoadContentAsync()
        {
            await Task.Yield();

            await LoadSoundsAsync();
            await LoadContentAsyncImpl();

            await Task.WhenAll(
                LoadAllAssetsAsync(),
                LoadFontsAndTexturesAsync());

            LoadAllSaves();
            ChangeLanguage(Game.Preferences.Language);

            CallAfterLoadContent = true;
        }

        /// <summary>
        /// Called after the content was loaded back from the main thread.
        /// </summary>
        public virtual void AfterContentLoadedFromMainThread()
        {
            using PerfTimeRecorder recorder = new("Preloading Fonts");
            PreloadFontTextures();
        }

        protected virtual Task LoadContentAsyncImpl() => Task.CompletedTask;

        protected virtual void PreloadContentImpl()
        {
            using PerfTimeRecorder recorder = new($"Loading Preload Assets");

            string path = Path.Join(PublishedPackedAssetsFullPath, PreloadPackedGameData.Name);
            if (!File.Exists(path))
            {
                GameLogger.Warning("Unable to preload content. Did you pack the game assets?");

                throw new InvalidOperationException("Unable to find preload content.");
            }

            PreloadPackedGameData? data = FileManager.UnpackContent<PreloadPackedGameData>(path);
            if (data is null)
            {
                return;
            }

            foreach (GameAsset asset in data.Assets)
            {
                AddAsset(asset);

                if (asset is SpriteAsset spriteAsset)
                {
                    FetchAtlas(spriteAsset.Atlas).LoadTextures();
                }
            }
        }

        /// <summary>
        /// Immediately fired once the "fast" loading finishes.
        /// </summary>
        protected virtual void OnAfterPreloadLoaded() { }

        protected virtual async Task LoadAllAssetsAsync()
        {
            using PerfTimeRecorder recorder = new($"Loading All Assets");

            await Task.Yield();

            string path = Path.Join(PublishedPackedAssetsFullPath, PackedGameData.Name);
            if (!File.Exists(path))
            {
                GameLogger.Warning("Unable to load game content. Did you pack the game assets?");

                throw new InvalidOperationException("Unable to find game content.");
            }

            PackedGameData? data = FileManager.UnpackContent<PackedGameData>(path);
            if (data is null)
            {
                return;
            }

            foreach (GameAsset asset in data.Assets)
            {
                AddAsset(asset);

                if (asset is FontAsset font)
                {
                    TrackFont(font);
                }

                if (asset is SpriteAsset sprite)
                {
                    _referencedAtlases.Add(sprite.Atlas);
                }
            }
        }

        protected virtual Task LoadFontsAndTexturesAsync() => Task.CompletedTask;

        protected void TrackFont(FontAsset asset)
        {
            PixelFont font = new(asset);

            lock (_fonts)
            {
                if (_fonts.ContainsKey(font.Index))
                {
                    GameLogger.Error($"Unable to load font: {asset.Name}. Duplicate index found!");
                    return;
                }

                GameLogger.LogDebug($"Tracking font: {font.Index}");
                _fonts = _fonts.Add(font.Index, font);
            }
        }

        /// <summary>
        /// Must be called on the UI thread, for now. Preload the font textures.
        /// </summary>
        private void PreloadFontTextures()
        {
            using PerfTimeRecorder recorder = new($"Loading Fonts and Atlas");

            lock (_fonts)
            {
                foreach ((_, PixelFont f) in _fonts)
                {
                    f.Preload();
                }
            }

            foreach (AtlasId id in _referencedAtlases)
            {
                FetchAtlas(id).LoadTextures();
            }
        }

        /// <summary>
        /// Override this to load all shaders present in the game.
        /// </summary>
        /// <param name="breakOnFail">Whether we should break if this fails.</param>
        /// <param name="forceReload">Whether we should force the reload (or recompile) of shaders.</param>
        public void LoadShaders(bool breakOnFail, bool forceReload = false)
        {
            using PerfTimeRecorder recorder = new("Loading Shaders");

            Effect? result;

            if (LoadShader("sprite2d", out result, breakOnFail, forceReload)) ShaderSprite = result;
            if (LoadShader("simple", out result, breakOnFail, forceReload)) ShaderSimple = result;
            if (LoadShader("pixel_art", out result, breakOnFail, forceReload)) ShaderPixel = result;

            if (_game is IShaderProvider { Shaders.Length: > 0 } provider)
            {
                CustomGameShaders = new Effect[provider.Shaders.Length];
                for (int i = 0; i < provider.Shaders.Length; i++)
                {
                    if (LoadShader(provider.Shaders[i], out var shader, breakOnFail, forceReload))
                    {
                        CustomGameShaders[i] = shader;
                    }
                }
            }
        }

        public virtual void InitShaders() { }

        /// <summary>
        /// Load and return shader of name <paramref name="name"/>.
        /// </summary>
        public bool LoadShader(string name, [NotNullWhen(true)] out Effect? effect, bool breakOnFail, bool forceReload)
        {
            GameLogger.Verify(_packedBinDirectoryPath is not null, "Why hasn't LoadContent() been called?");

            Effect? shaderFromFile = null;
            if (forceReload || !TryLoadShaderFromFile(name, out shaderFromFile))
            {
                if (TryCompileShader(name, out Effect? compiledShader))
                {
                    effect = compiledShader;
                    effect.Name = name;
                    if (effect.Techniques.FirstOrDefault()?.Name == "DefaultTechnique")
                    {
                        effect.SetTechnique("DefaultTechnique");
                    }
                    return true;
                }
            }

            if (shaderFromFile is not null)
            {
                effect = shaderFromFile;
                effect.Name = name;
                if (effect.Techniques.FirstOrDefault()?.Name == "DefaultTechnique")
                {
                    effect.SetTechnique("DefaultTechnique");
                }
                return true;
            }

            if (breakOnFail)
            {
                throw new InvalidOperationException("Unable to compile shader!");
            }

            effect = null;
            return false;
        }

        protected virtual bool TryCompileShader(string name, [NotNullWhen(true)] out Effect? result)
        {
            result = null;
            return false;
        }

        private string OutputPathForShaderOfName(string name, string? path = default)
        {
            GameLogger.Verify(_packedBinDirectoryPath is not null, "Why hasn't LoadContent() been called?");
            return Path.Join(path ?? _packedBinDirectoryPath, string.Format(ShaderRelativePath, name));
        }

        private bool TryLoadShaderFromFile(string name, [NotNullWhen(true)] out Effect? result)
        {
            result = null;

            string shaderPath = OutputPathForShaderOfName(name);
            if (!File.Exists(shaderPath))
            {
                return false;
            }

            try
            {
                result = new Effect(Game.GraphicsDevice, File.ReadAllBytes(shaderPath));
            }
            catch
            {
                GameLogger.Error($"Error loading file: {shaderPath}");
                return false;
            }

            return true;
        }

        private void LoadGameSettings()
        {
            string gameProfilePath = FileHelper.GetPath(Path.Join(_binResourcesDirectory, GameProfileFileName));

            if (_gameProfile is null && File.Exists(gameProfilePath))
            {
                GameProfile = (GameProfile)FileManager.DeserializeAsset<GameAsset>(gameProfilePath)!;
                GameLogger.Log("Successfully loaded game profile settings.");
            }
            else if (_gameProfile is null)
            {
                GameLogger.Error("Unable to find the game profile, using a default one. Report this issue immediately!");

                GameProfile = CreateGameProfile();
                GameProfile.MakeGuid();
            }
        }

        public MonoWorld CreateWorldInstanceFromSave(Guid guid, Camera2D camera)
        {
            if (TryGetAsset<WorldAsset>(guid) is WorldAsset world)
            {
                // If there is a saved run for this map, run from this!
                if (TryGetActiveSaveData()?.TryLoadLevel(guid) is SavedWorld savedWorld)
                {
                    return world.CreateInstanceFromSave(savedWorld, camera, FetchSystemsToStartWith());
                }

                // Otherwise, fallback to default world instances.
                return world.CreateInstance(camera, FetchSystemsToStartWith());
            }

            GameLogger.Error($"World asset with guid '{guid}' not found or is corrupted.");
            throw new InvalidOperationException($"World asset with guid '{guid}' not found or is corrupted.");
        }

        /// <summary>
        /// This has the collection of systems which will be added to any world that will be created.
        /// Used when hooking new systems into the editor.
        /// </summary>
        protected virtual ImmutableArray<(Type, bool)> FetchSystemsToStartWith() => ImmutableArray<(Type, bool)>.Empty;

        /// <summary>
        /// This will skip loading assets that start with a certain char. This is used to filter assets
        /// that are only used in the editor.
        /// </summary>
        protected virtual bool ShouldSkipAsset(string fullFilename)
        {
            if (Path.GetFileName(fullFilename).StartsWith(SKIP_CHAR))
            {
                return true;
            }

            return IsPathOnSkipLoading(fullFilename);
        }

        public bool IsPathOnSkipLoading(string name)
        {
            // This is okay because the paths length should be very short (3-5).
            foreach (string path in _skipLoadingAssetAtPaths)
            {
                if (name.Contains(path))
                {
                    return true;
                }
            }

            return false;
        }

        public void SkipLoadingAssetsAt(string path)
        {
            lock (_skipLoadingAssetAtPaths)
            {
                _skipLoadingAssetAtPaths.Add(path);
            }
        }

        public void OnErrorLoadingAsset() => _errorLoadingLastAsset = true;

        /// <summary>
        /// Let implementations deal with a custom handling of errors.
        /// This is called when the asset was successfully loaded but failed to fill some of its fields.
        /// </summary>
        protected virtual void OnAssetLoadError(GameAsset asset) { }

        public GameAsset? TryLoadAsset(string path, string relativePath, bool skipFailures = true, bool hasEditorPath = false)
        {
            GameAsset? asset;

            try
            {
                asset = FileManager.DeserializeAsset<GameAsset>(path);
            }
            catch (Exception ex) when (skipFailures)
            {
                GameLogger.Warning($"Error loading [{path}]:{ex}");
                return null;
            }
            
            if (_errorLoadingLastAsset)
            {
                _errorLoadingLastAsset = false;
                GameLogger.Warning($"Error loading data at '{path}'.");

                if (asset is not null)
                {
                    OnAssetLoadError(asset);
                }
            }

            if (asset is null)
            {
                if (!skipFailures)
                {
                    GameLogger.Warning($"Unable to deserialize {path}.");
                }

                return null;
            }

            if (!asset.IsStoredInSaveData)
            {
                string finalRelative = hasEditorPath ?
                    FileHelper.GetPath(relativePath) :
                    FileHelper.GetPath(Path.Join(relativePath, Serialization.FileHelper.Clean(asset.EditorFolder)));

                asset.FilePath = Path.GetRelativePath(finalRelative, path).EscapePath();
            }
            else
            {
                // For save files, just use the full path. We don't want to be smart about it at this point, as
                // we don't have to keep data back and forth from different relative paths.
                asset.FilePath = path;
            }

            return asset;
        }

        public async Task<GameAsset?> TryLoadAssetAsync(string path, string relativePath, bool skipFailures = true, bool hasEditorPath = false)
        {
            GameAsset? asset;

            try
            {
                asset = await FileManager.DeserializeAssetAsync<GameAsset>(path);
            }
            catch (Exception ex) when (skipFailures)
            {
                GameLogger.Warning($"Error loading [{path}]:{ex}");
                return null;
            }

            if (_errorLoadingLastAsset)
            {
                _errorLoadingLastAsset = false;
                GameLogger.Warning($"Error loading data at '{path}'.");

                if (asset is not null)
                {
                    OnAssetLoadError(asset);
                }
            }

            if (asset is null)
            {
                if (!skipFailures)
                {
                    GameLogger.Warning($"Unable to deserialize {path}.");
                }

                return null;
            }

            if (!asset.IsStoredInSaveData)
            {
                string finalRelative = hasEditorPath ?
                    FileHelper.GetPath(relativePath) :
                    FileHelper.GetPath(Path.Join(relativePath, Serialization.FileHelper.Clean(asset.EditorFolder)));

                asset.FilePath = Path.GetRelativePath(finalRelative, path).EscapePath();
            }
            else
            {
                // For save files, just use the full path. We don't want to be smart about it at this point, as
                // we don't have to keep data back and forth from different relative paths.
                asset.FilePath = path;
            }

            return asset;
        }

        public void RemoveAsset<T>(T asset) where T : GameAsset
        {
            RemoveAsset(asset.GetType(), asset.Guid);
        }

        public void RemoveAsset<T>(Guid assetGuid) where T : GameAsset
        {
            RemoveAsset(typeof(T), assetGuid);
        }

        protected virtual void RemoveAsset(Type t, Guid assetGuid)
        {
            if (!_allAssets.ContainsKey(assetGuid) || !_database.TryGetValue(t, out var databaseSet) || !databaseSet.Contains(assetGuid))
            {
                throw new ArgumentException($"Can't remove asset {assetGuid} from database.");
            }

            _allAssets.Remove(assetGuid);
            databaseSet.Remove(assetGuid);

            OnAssetRenamedOrAddedOrDeleted();
        }

        public void AddAsset<T>(T asset, bool overwriteDuplicateGuids = false) where T : GameAsset
        {
            lock (AssetsLock)
            {
                if (!asset.StoreInDatabase)
                {
                    // Do not add the asset.
                    return;
                }

                if (asset.Guid == Guid.Empty)
                {
                    asset.MakeGuid();
                }

                if (string.IsNullOrWhiteSpace(asset.Name))
                {
                    asset.Name = asset.Guid.ToString();
                }

                // T might correspond to an abstract type.
                // Get the actual implementation type.
                Type t = asset.GetType();
                if (!_database.TryGetValue(t, out HashSet<Guid>? databaseSet))
                {
                    databaseSet = new();

                    _database[t] = databaseSet;
                }

                if (!overwriteDuplicateGuids)
                {
                    if (databaseSet.Contains(asset.Guid) || _allAssets.ContainsKey(asset.Guid))
                    {
                        GameLogger.Error(
                            $"Duplicate asset GUID detected '{_allAssets[asset.Guid].EditorFolder.TrimStart('#')}\\{_allAssets[asset.Guid].FilePath}, {asset.EditorFolder.TrimStart('#')}\\{asset.FilePath}'(GUID:{_allAssets[asset.Guid].Guid})");
                        return;
                    }
                }

                databaseSet.Add(asset.Guid);
                _allAssets[asset.Guid] = asset;

                OnAssetRenamedOrAddedOrDeleted();
            }
        }
        public bool HasAsset<T>(Guid id) where T : GameAsset =>
            _database.TryGetValue(typeof(T), out HashSet<Guid>? assets) && assets.Contains(id);

        public T? TryGetAsset<T>(Guid id) where T : GameAsset
        {
            if (TryGetAsset(id) is T asset)
            {
                return asset;
            }

            return default;
        }
        public PrefabAsset GetPrefab(Guid id) => GetAsset<PrefabAsset>(id);
        public PrefabAsset? TryGetPrefab(Guid id) => TryGetAsset<PrefabAsset>(id);

        /// <summary>
        /// Quick and dirty way to get a aseprite frame, animated when you don't want to deal with the animation system.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public AtlasCoordinates GetAsepriteFrame(Guid id)
        {
            var asset = Game.Data.GetAsset<SpriteAsset>(id);
            return asset.Frames[asset.Animations.First().Value.Evaluate(Game.Now, true).Frame];
        }

        public T GetAsset<T>(Guid id) where T : GameAsset
        {
            if (TryGetAsset<T>(id) is T asset)
            {
                return asset;
            }

            if (typeof(T) == typeof(SpriteAsset))
            {
                // This is very common in our engine, so, for sprites in specific, display a missing image instead.
                if (_gameProfile is not null && TryGetAsset<T>(_gameProfile.MissingImage) is T missingImageAsset)
                {
                    return missingImageAsset;
                }
            }

            throw new ArgumentException($"Unable to find the asset of type {typeof(T).Name} with id: {id} in database.");
        }

        public GameAsset GetAsset(Guid id)
        {
            if (TryGetAsset(id) is GameAsset asset)
            {
                return asset;
            }

            throw new ArgumentException($"Unable to find the asset with id: {id} in database.");
        }

        /// <summary>
        /// Get a generic asset with a <paramref name="id"/>.
        /// </summary>
        public GameAsset? TryGetAsset(Guid id)
        {
            if (_allAssets.TryGetValue(id, out GameAsset? asset))
            {
                return asset;
            }

            return default;
        }

        public IEnumerable<GameAsset> GetAllAssets() => _allAssets.Values;

        /// <summary>
        /// Find all the assets names for an asset type <paramref name="t"/>.
        /// </summary>
        /// <param name="t">The type that inherits from <see cref="GameAsset"/>.</param>
        public ImmutableHashSet<string> FindAllNamesForAsset(Type t)
        {
            ImmutableHashSet<string> result = ImmutableHashSet<string>.Empty;

            if (_database.TryGetValue(t, out HashSet<Guid>? assetGuids))
            {
                result = assetGuids.Select(g => _allAssets[g].Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        public ImmutableDictionary<Guid, GameAsset> FilterAllAssets(params Type[] types)
        {
            var builder = ImmutableDictionary.CreateBuilder<Guid, GameAsset>();

            foreach (var t in types)
            {
                if (_database.TryGetValue(t, out HashSet<Guid>? assetGuids))
                {
                    builder.AddRange(assetGuids.ToDictionary(id => id, id => _allAssets[id]));
                }
            }

            return builder.ToImmutableDictionary();
        }

        /// <summary>
        /// Return all the assets except the ones in <paramref name="types"/>.
        /// </summary>
        public ImmutableDictionary<Guid, GameAsset> FilterOutAssets(params Type[] types)
        {
            var builder = ImmutableDictionary.CreateBuilder<Guid, GameAsset>();

            foreach (Type type in _database.Keys)
            {
                if (!types.Contains(type))
                {
                    builder.AddRange(FilterAllAssets(type));
                }
            }

            return builder.ToImmutableDictionary();
        }

        public PixelFont GetFont(int index)
        {
            if (_fonts.TryGetValue(index, out PixelFont? font))
            {
                return font;
            }

            if (_fonts.FirstOrDefault().Value is PixelFont firstFont)
            {
                GameLogger.Error($"Unable to find font with index {index}.");
                return firstFont;
            }

            throw new ArgumentException($"Unable to find font with index {index}.");
        }

        public virtual void Dispose()
        {
            DisposeAtlases();
        }

        public virtual void OnAssetRenamedOrAddedOrDeleted() { }

        public virtual void TrackOnHotReloadSprite(Action action) { }

        public virtual void UntrackOnHotReloadSprite(Action action) { }

        public Texture2D FetchTexture(string path)
        {
            if (CachedUniqueTextures.TryGetValue(path, out Texture2D? value))
            {
                return value;
            }

            string fullPath = Path.Join(_packedBinDirectoryPath, $"{path.EscapePath()}{TextureServices.QOI_GZ_EXTENSION}");
            if (!File.Exists(fullPath))
            {
                // We also support .png
                fullPath = Path.Join(_packedBinDirectoryPath, $"{path.EscapePath()}{TextureServices.PNG_EXTENSION}");
            }

            Texture2D texture = TextureServices.FromFile(Game.GraphicsDevice, fullPath);

            texture.Name = path;
            CachedUniqueTextures[path] = texture;

            return texture;
        }

        public TextureAtlas FetchAtlas(AtlasId atlas, bool warnOnError = true)
        {
            if (atlas == AtlasId.None)
            {
                throw new ArgumentException("There's no atlas to fetch.");
            }

            if (!LoadedAtlasses.ContainsKey(atlas))
            {
                string filepath = Path.Join(_packedBinDirectoryPath, GameProfile.AtlasFolderName, $"{atlas.GetDescription()}.json");
                TextureAtlas? newAtlas = FileManager.DeserializeGeneric<TextureAtlas>(filepath, warnOnError);

                if (newAtlas is not null)
                {
                    LoadedAtlasses[atlas] = newAtlas;
                }
                else
                {
                    throw new ArgumentException($"Atlas {atlas} is not loaded and couldn't be loaded from '{filepath}'.");
                }
            }

            return LoadedAtlasses[atlas];
        }

        public TextureAtlas? TryFetchAtlas(AtlasId atlas)
        {
            if (atlas == AtlasId.None)
            {
                return null;
            }

            if (!LoadedAtlasses.TryGetValue(atlas, out TextureAtlas? texture))
            {
                string path = Path.Join(_packedBinDirectoryPath, GameProfile.AtlasFolderName, $"{atlas.GetDescription()}.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                texture = FileManager.DeserializeGeneric<TextureAtlas>(path, warnOnErrors: false);

                if (texture is not null)
                {
                    LoadedAtlasses[atlas] = texture;
                }
                else
                {
                    return null;
                }
            }

            return texture;
        }

        public void ReplaceAtlas(AtlasId atlasId, TextureAtlas newAtlas)
        {
            if (LoadedAtlasses.TryGetValue(atlasId, out var texture))
            {
                texture.Dispose();
            }

            LoadedAtlasses[atlasId] = newAtlas;
        }

        public void DisposeAtlas(AtlasId atlasId)
        {
            if (LoadedAtlasses.TryGetValue(atlasId, out var texture))
            {
                texture.Dispose();
            }

            LoadedAtlasses.Remove(atlasId);
        }

        public void DisposeAtlases()
        {
            foreach (var atlas in LoadedAtlasses)
            {
                atlas.Value?.Dispose();
            }

            LoadedAtlasses.Clear();
        }
    }
}