using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines;
using DailyRoutines.Windows;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace MahjongHelper;

public unsafe class Helper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "麻将助手",
        Description = "金蝶麻将助手",
        Category    = ModuleCategories.Assist,
        Author = ["Fragile"]
    };
    
    private ImportantPointers ImportantPointers { get; }
    private NodeCrawlerUtils NodeCrawlerUtils { get; }
    private Task WindowUpdateTask { get; set; } = null!;
    private static Config ModuleConfig = null!;
    
    // 存储观察到的牌信息
    public List<ObservedTile> ObservedTiles { get; private set; } = new List<ObservedTile>();
    public Dictionary<string, int> RemainingMap { get; private set; } = new Dictionary<string, int>();
    public Dictionary<string, int> SuitCounts { get; private set; } = new Dictionary<string, int>();
    
    // 鸣牌事件相关
    private Dictionary<MahjongNodeType, List<MeldGroup>> PreviousMeldGroups { get; set; } = new Dictionary<MahjongNodeType, List<MeldGroup>>();
    public List<MeldEvent> RecentMeldEvents { get; private set; } = new List<MeldEvent>();
    
    // 鸣牌提示相关
    public List<MeldSuggestion> CurrentMeldSuggestions { get; private set; } = new List<MeldSuggestion>();
    private TileTexture? LastDiscardedTile { get; set; } = null;
    private MahjongNodeType LastDiscardPlayer { get; set; } = MahjongNodeType.PLAYER_DISCARD_TILE;
    
    // 河牌状态跟踪（用于检测新弃牌）
    private Dictionary<MahjongNodeType, List<string>> PreviousDiscardPiles { get; set; } = new Dictionary<MahjongNodeType, List<string>>();
    
    // 事件委托
    public event Action<MeldEvent>? OnMeldEvent;
    public event Action<List<MeldSuggestion>>? OnMeldSuggestionsUpdated;
    
    public Helper()
    {
        ImportantPointers = new ImportantPointers(DService.Log);
        NodeCrawlerUtils = new NodeCrawlerUtils(DService.Log);
    }
    
    public override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        Overlay ??= new Overlay(this);
        
        // 注册麻将界面的生命周期事件
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Emj", OnAddonPostSetup);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonPostSetup);
        DService.AddonLifecycle.UnregisterListener(OnAddonPostRefresh);
        DService.AddonLifecycle.UnregisterListener(OnAddonPreFinalize);
        
        TaskHelper?.Abort();
        base.Uninit();
    }
    
    #region 界面监控
    
    private unsafe void OnAddonPostSetup(AddonEvent type, AddonArgs args) 
    {
        var addonPtr = args.Addon;
        if (addonPtr == IntPtr.Zero) 
        {
            DService.Log.Info("无法找到麻将界面 Emj");
            return;
        }
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Emj", OnAddonPostRefresh);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Emj", OnAddonPreFinalize);

        var addon = (AtkUnitBase*)addonPtr;
        var rootNode = addon->RootNode;
        ImportantPointers.WipePointers();
        NodeCrawlerUtils.TraverseAllAtkResNodes(rootNode, (intPtr) => ImportantPointers.MaybeTrackPointer(intPtr));
        
        // 重置鸣牌事件历史
        ResetMeldEvents();
        
        // 打开悬浮窗
        Overlay.IsOpen = true;
    }

    private void OnAddonPreFinalize(AddonEvent type, AddonArgs args) 
    {
        // 麻将界面关闭时关闭悬浮窗
        Overlay.IsOpen = false;
    }

    private void OnAddonPostRefresh(AddonEvent type, AddonArgs args) 
    {
        var addonPtr = args.Addon;
        if (addonPtr == IntPtr.Zero) 
        {
            DService.Log.Info("无法找到麻将界面 Emj");
            return;
        }

        if (WindowUpdateTask == null || WindowUpdateTask.IsCompleted || WindowUpdateTask.IsFaulted || WindowUpdateTask.IsCanceled) 
        {
            DService.Log.Info("更新麻将信息");
            WindowUpdateTask = Task.Run(WindowUpdater);
        }
    }
    
    #endregion

    #region 信息处理
    
    private unsafe void WindowUpdater() 
    {
        var observedTiles = GetObservedTiles();
        var remainingMap = TileTextureUtilities.TileCountTracker.RemainingFromObserved(observedTiles);
        var suitCounts = new Dictionary<string, int>();
        
        foreach (var kvp in remainingMap) 
        {
            var suit = kvp.Key.Substring(1, 1);
            if (suit == Suit.HONOR) 
            {
                continue;
            }
            
            if (suitCounts.ContainsKey(suit)) 
            {
                suitCounts[suit] += kvp.Value;
            } 
            else 
            {
                suitCounts.Add(suit, kvp.Value);
            }
        }

        ObservedTiles = observedTiles;
        RemainingMap = remainingMap;
        SuitCounts = suitCounts;
        
        // 检测鸣牌事件
        DetectMeldEvents();
        
        // 检测鸣牌提示
        DetectMeldSuggestions();
    }
    
    private unsafe List<ObservedTile> GetObservedDiscardTiles(List<IntPtr> ptrs, MahjongNodeType playerArea) 
    {
        var observedTileTextures = new List<ObservedTile>(); 
        ptrs.ForEach(ptr => 
        {
            var castedPtr = (AtkResNode*)ptr;
            var tileTexture = NodeCrawlerUtils.GetTileTextureFromDiscardTile(ptr);
            if (tileTexture != null) 
            {
                if (!tileTexture.IsMelded) 
                {
                    observedTileTextures.Add(new ObservedTile(playerArea, tileTexture.TileTexture));
                }
            }
        });
        return observedTileTextures;
    }

    private unsafe List<ObservedTile> GetObservedMeldTiles(List<IntPtr> ptrs, MahjongNodeType playerArea) 
    {
        var observedTileTextures = new List<ObservedTile>();
        ptrs.ForEach(ptr => 
        {
            var castedPtr = (AtkResNode*)ptr;
            var tileTextures = NodeCrawlerUtils.GetTileTexturesFromMeldGroup(ptr);
            tileTextures?.ForEach(texture => observedTileTextures.Add(new ObservedTile(playerArea, texture)));
        });
        return observedTileTextures;
    }

    public unsafe List<ObservedTile> GetObservedTiles() 
    {
        var observedTileTextures = new List<ObservedTile>();
        
        // 获取玩家手牌
        ImportantPointers.PlayerHand.ForEach(ptr => 
        {
            var castedPtr = (AtkResNode*)ptr;
            var tileTexture = NodeCrawlerUtils.GetTileTextureFromPlayerHandTile(ptr);
            if (tileTexture != null) 
            {
                observedTileTextures.Add(new ObservedTile(MahjongNodeType.PLAYER_HAND_TILE, tileTexture));
            }
        });

        // 获取所有玩家的弃牌
        observedTileTextures.AddRange(GetObservedDiscardTiles(ImportantPointers.PlayerDiscardPile, MahjongNodeType.PLAYER_DISCARD_TILE));
        observedTileTextures.AddRange(GetObservedDiscardTiles(ImportantPointers.RightDiscardPile, MahjongNodeType.RIGHT_DISCARD_TILE));
        observedTileTextures.AddRange(GetObservedDiscardTiles(ImportantPointers.FarDiscardPile, MahjongNodeType.FAR_DISCARD_TILE));
        observedTileTextures.AddRange(GetObservedDiscardTiles(ImportantPointers.LeftDiscardPile, MahjongNodeType.LEFT_DISCARD_TILE));

        // 获取所有玩家的鸣牌组
        ImportantPointers.PlayerMeldGroups.ForEach(ptr => 
        {
            var castedPtr = (AtkResNode*)ptr;
            var tileTextures = NodeCrawlerUtils.GetTileTexturesFromPlayerMeldGroup(ptr);
            tileTextures?.ForEach(texture => observedTileTextures.Add(new ObservedTile(MahjongNodeType.PLAYER_MELD_GROUP, texture)));
        });

        // 获取其他玩家的鸣牌组
        observedTileTextures.AddRange(GetObservedMeldTiles(ImportantPointers.RightMeldGroups, MahjongNodeType.RIGHT_MELD_GROUP));
        observedTileTextures.AddRange(GetObservedMeldTiles(ImportantPointers.FarMeldGroups, MahjongNodeType.FAR_MELD_GROUP));
        observedTileTextures.AddRange(GetObservedMeldTiles(ImportantPointers.LeftMeldGroups, MahjongNodeType.LEFT_MELD_GROUP));
        
        return observedTileTextures;
    }
    
    #endregion
    
    #region 模块界面
    
    public override void ConfigUI()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.6f, 1.0f, 1.0f), "麻将助手设置");
        ImGui.Separator();
        
        if (ImGui.Checkbox("自动显示悬浮窗", ref ModuleConfig.AutoShowOverlay))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示剩余牌数量", ref ModuleConfig.ShowRemainingCount))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示手牌信息", ref ModuleConfig.ShowHandTiles))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示河牌信息", ref ModuleConfig.ShowDiscardTiles))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示鸣牌信息", ref ModuleConfig.ShowMeldTiles))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示鸣牌事件", ref ModuleConfig.ShowMeldEvents))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示鸣牌类型详情", ref ModuleConfig.ShowMeldTypeDetails))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("显示鸣牌提示", ref ModuleConfig.ShowMeldSuggestions))
            ModuleConfig.Save(this);
            
        if (ImGui.Checkbox("突出显示紧急提示", ref ModuleConfig.HighlightUrgentSuggestions))
            ModuleConfig.Save(this);
    }
    
    public override void OverlayUI()
    {
        var emjAddon = GetAddonByName("Emj");
        if (emjAddon == null) return;
        
        // 设置悬浮窗位置
        var pos = new Vector2(emjAddon->GetX() + emjAddon->Scale * emjAddon->RootNode->Width + 10, emjAddon->GetY());
        ImGui.SetWindowPos(pos);
        
        ImGui.TextColored(new Vector4(0.2f, 0.6f, 1.0f, 1.0f), "麻将助手");
        ImGui.Separator();
        
        // 显示各种花色的剩余数量
        if (ModuleConfig.ShowRemainingCount)
        {
            using var suitNode = ImRaii.TreeNode("剩余牌数量");
            if (suitNode)
            {
                if (SuitCounts.TryGetValue(Suit.MAN, out var manCount))
                    ImGui.Text($"万子: {manCount}");
                    
                if (SuitCounts.TryGetValue(Suit.PIN, out var pinCount))
                    ImGui.Text($"筒子: {pinCount}");
                    
                if (SuitCounts.TryGetValue(Suit.SOU, out var souCount))
                    ImGui.Text($"索子: {souCount}");
                
                // 计算字牌剩余数量
                var honorCount = 0;
                for (int i = 1; i < 8; i++)
                {
                    var notation = $"{i}{Suit.HONOR}";
                    if (RemainingMap.TryGetValue(notation, out var count))
                        honorCount += count;
                }
                ImGui.Text($"字牌: {honorCount}");
            }
        }
        
        // 显示手牌信息
        if (ModuleConfig.ShowHandTiles)
        {
            using var handNode = ImRaii.TreeNode("手牌");
            if (handNode)
            {
                var handTiles = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.PLAYER_HAND_TILE).ToList();
                if (handTiles.Count > 0)
                {
                    ImGui.Text($"手牌数量: {handTiles.Count}");
                    ImGui.Text($"手牌: {string.Join(", ", handTiles.Select(t => t.TileTexture.MjaiNotation))}");
                }
                else
                {
                    ImGui.Text("未检测到手牌");
                }
            }
        }
        
        // 显示河牌信息
        if (ModuleConfig.ShowDiscardTiles)
        {
            using var discardNode = ImRaii.TreeNode("河牌");
            if (discardNode)
            {
                // 自家河牌
                var playerDiscards = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.PLAYER_DISCARD_TILE).ToList();
                if (playerDiscards.Count > 0)
                {
                    ImGui.Text($"自家河牌: {string.Join(", ", playerDiscards.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 下家河牌
                var rightDiscards = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.RIGHT_DISCARD_TILE).ToList();
                if (rightDiscards.Count > 0)
                {
                    ImGui.Text($"下家河牌: {string.Join(", ", rightDiscards.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 对家河牌
                var farDiscards = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.FAR_DISCARD_TILE).ToList();
                if (farDiscards.Count > 0)
                {
                    ImGui.Text($"对家河牌: {string.Join(", ", farDiscards.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 上家河牌
                var leftDiscards = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.LEFT_DISCARD_TILE).ToList();
                if (leftDiscards.Count > 0)
                {
                    ImGui.Text($"上家河牌: {string.Join(", ", leftDiscards.Select(t => t.TileTexture.MjaiNotation))}");
                }
            }
        }
        
        // 显示鸣牌信息
        if (ModuleConfig.ShowMeldTiles)
        {
            using var meldNode = ImRaii.TreeNode("鸣牌");
            if (meldNode)
            {
                // 自家鸣牌
                var playerMelds = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.PLAYER_MELD_GROUP).ToList();
                if (playerMelds.Count > 0)
                {
                    ImGui.Text($"自家鸣牌: {string.Join(", ", playerMelds.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 下家鸣牌
                var rightMelds = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.RIGHT_MELD_GROUP).ToList();
                if (rightMelds.Count > 0)
                {
                    ImGui.Text($"下家鸣牌: {string.Join(", ", rightMelds.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 对家鸣牌
                var farMelds = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.FAR_MELD_GROUP).ToList();
                if (farMelds.Count > 0)
                {
                    ImGui.Text($"对家鸣牌: {string.Join(", ", farMelds.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 上家鸣牌
                var leftMelds = ObservedTiles.Where(t => t.PlayerArea == MahjongNodeType.LEFT_MELD_GROUP).ToList();
                if (leftMelds.Count > 0)
                {
                    ImGui.Text($"上家鸣牌: {string.Join(", ", leftMelds.Select(t => t.TileTexture.MjaiNotation))}");
                }
                
                // 显示鸣牌类型详情
                if (ModuleConfig.ShowMeldTypeDetails)
                {
                    ImGui.Separator();
                    var currentMeldGroups = GetCurrentMeldGroups();
                    
                    foreach (var kvp in currentMeldGroups)
                    {
                        var playerName = kvp.Key switch
                        {
                            MahjongNodeType.PLAYER_MELD_GROUP => "自家",
                            MahjongNodeType.RIGHT_MELD_GROUP => "下家",
                            MahjongNodeType.FAR_MELD_GROUP => "对家",
                            MahjongNodeType.LEFT_MELD_GROUP => "上家",
                            _ => "未知"
                        };
                        
                        foreach (var meld in kvp.Value)
                        {
                            var meldTypeName = meld.Type switch
                            {
                                MeldType.Chi => "吃",
                                MeldType.Pon => "碰",
                                MeldType.Kan => "杠",
                                _ => "未知"
                            };
                            
                            var tiles = string.Join(", ", meld.Tiles.Select(t => t.MjaiNotation));
                            ImGui.Text($"{playerName} {meldTypeName}: {tiles}");
                        }
                    }
                }
            }
        }
        
        // 显示鸣牌事件
        if (ModuleConfig.ShowMeldEvents)
        {
            using var eventNode = ImRaii.TreeNode("鸣牌事件");
            if (eventNode)
            {
                if (RecentMeldEvents.Count > 0)
                {
                    ImGui.Text($"最近 {RecentMeldEvents.Count} 个鸣牌事件:");
                    ImGui.Separator();
                    
                    // 按时间倒序显示最近的事件
                    var sortedEvents = RecentMeldEvents.OrderByDescending(e => e.Timestamp).ToList();
                    
                    foreach (var meldEvent in sortedEvents)
                    {
                        var timeStr = meldEvent.Timestamp.ToString("HH:mm:ss");
                        var color = meldEvent.MeldGroup.Type switch
                        {
                            MeldType.Chi => new Vector4(0.2f, 0.8f, 0.2f, 1.0f), // 绿色 - 吃
                            MeldType.Pon => new Vector4(0.2f, 0.6f, 1.0f, 1.0f), // 蓝色 - 碰
                            MeldType.Kan => new Vector4(1.0f, 0.6f, 0.2f, 1.0f), // 橙色 - 杠
                            _ => new Vector4(0.8f, 0.8f, 0.8f, 1.0f) // 灰色 - 未知
                        };
                        
                        ImGui.TextColored(color, $"[{timeStr}] {meldEvent}");
                    }
                }
                else
                {
                    ImGui.Text("暂无鸣牌事件");
                }
            }
        }
        
        // 显示鸣牌提示
        if (ModuleConfig.ShowMeldSuggestions)
        {
            using var suggestionNode = ImRaii.TreeNode("鸣牌提示");
            if (suggestionNode)
            {
                if (CurrentMeldSuggestions.Count > 0)
                {
                    ImGui.Text($"当前可用操作 ({CurrentMeldSuggestions.Count} 个):");
                    ImGui.Separator();
                    
                    foreach (var suggestion in CurrentMeldSuggestions)
                    {
                        var color = suggestion.Type switch
                        {
                            MeldType.Chi => new Vector4(0.2f, 0.8f, 0.2f, 1.0f), // 绿色 - 吃
                            MeldType.Pon => new Vector4(0.2f, 0.6f, 1.0f, 1.0f), // 蓝色 - 碰
                            MeldType.Kan => new Vector4(1.0f, 0.6f, 0.2f, 1.0f), // 橙色 - 明杠
                            MeldType.AnKan => new Vector4(0.8f, 0.2f, 0.8f, 1.0f), // 紫色 - 暗杠
                            MeldType.KaKan => new Vector4(1.0f, 0.8f, 0.2f, 1.0f), // 黄色 - 加杠
                            _ => new Vector4(0.8f, 0.8f, 0.8f, 1.0f) // 灰色 - 未知
                        };
                        
                        // 如果启用了突出显示紧急提示，且是对别人弃牌的反应
                        if (ModuleConfig.HighlightUrgentSuggestions && 
                            suggestion.SourcePlayer != MahjongNodeType.PLAYER_HAND_TILE)
                        {
                            // 使用更亮的颜色和加粗效果
                            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(color.X * 1.2f, color.Y * 1.2f, color.Z * 1.2f, 1.0f));
                            ImGui.Text($"🔔 {suggestion.Description}");
                            ImGui.PopStyleColor();
                        }
                        else
                        {
                            ImGui.TextColored(color, $"• {suggestion.Description}");
                        }
                        
                        // 显示详细的牌组信息
                        if (suggestion.RequiredTiles.Count > 0)
                        {
                            var requiredStr = string.Join(", ", suggestion.RequiredTiles.Select(t => t.MjaiNotation));
                            ImGui.SameLine();
                            ImGui.TextDisabled($"({requiredStr})");
                        }
                    }
                    
                    // 显示优先级提示
                    var urgentSuggestions = CurrentMeldSuggestions.Where(s => 
                        s.SourcePlayer != MahjongNodeType.PLAYER_HAND_TILE).ToList();
                    
                    if (urgentSuggestions.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), "⚠️ 需要立即决定!");
                        ImGui.TextWrapped("以上操作需要立即响应别人的弃牌");
                    }
                }
                else
                {
                    ImGui.Text("当前没有可用的鸣牌操作");
                }
            }
        }
    }
    
    #endregion
    
    #region 配置
    
    private class Config : ModuleConfiguration
    {
        public bool AutoShowOverlay = true;
        public bool ShowRemainingCount = true;
        public bool ShowHandTiles = true;
        public bool ShowDiscardTiles = true;
        public bool ShowMeldTiles = true;
        public bool ShowMeldEvents = true;
        public bool ShowMeldTypeDetails = true;
        public bool ShowMeldSuggestions = true;
        public bool HighlightUrgentSuggestions = true;
    }
    
    #endregion

    #region 鸣牌事件处理
    
    /// <summary>
    /// 鸣牌提示
    /// </summary>
    public class MeldSuggestion
    {
        public MeldType Type { get; set; }
        public List<TileTexture> RequiredTiles { get; set; } = new List<TileTexture>();
        public TileTexture? TargetTile { get; set; } // 目标牌（别人打出的或自己摸的）
        public MahjongNodeType SourcePlayer { get; set; } // 牌的来源玩家
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; }
        
        public MeldSuggestion(MeldType type, List<TileTexture> requiredTiles, TileTexture? targetTile, MahjongNodeType sourcePlayer)
        {
            Type = type;
            RequiredTiles = requiredTiles;
            TargetTile = targetTile;
            SourcePlayer = sourcePlayer;
            Timestamp = DateTime.Now;
            Description = GenerateDescription();
        }
        
        private string GenerateDescription()
        {
            var typeName = Type switch
            {
                MeldType.Chi => "吃",
                MeldType.Pon => "碰",
                MeldType.Kan => "杠",
                MeldType.AnKan => "暗杠",
                MeldType.KaKan => "加杠",
                _ => "未知"
            };
            
            var sourcePlayerName = SourcePlayer switch
            {
                MahjongNodeType.PLAYER_DISCARD_TILE => "自家",
                MahjongNodeType.RIGHT_DISCARD_TILE => "下家", 
                MahjongNodeType.FAR_DISCARD_TILE => "对家",
                MahjongNodeType.LEFT_DISCARD_TILE => "上家",
                MahjongNodeType.PLAYER_HAND_TILE => "手牌",
                _ => "未知"
            };
            
            if (TargetTile != null)
            {
                var requiredStr = string.Join(", ", RequiredTiles.Select(t => t.MjaiNotation));
                if (Type == MeldType.AnKan)
                {
                    return $"可以暗杠 {TargetTile.MjaiNotation}";
                }
                else if (Type == MeldType.KaKan)
                {
                    return $"可以加杠 {TargetTile.MjaiNotation}";
                }
                else
                {
                    return $"可以{typeName} {sourcePlayerName}的 {TargetTile.MjaiNotation} (用 {requiredStr})";
                }
            }
            
            return $"可以{typeName}";
        }
        
        public string GetTypeName()
        {
            return Type switch
            {
                MeldType.Chi => "吃",
                MeldType.Pon => "碰",
                MeldType.Kan => "杠",
                MeldType.AnKan => "暗杠", 
                MeldType.KaKan => "加杠",
                _ => "未知"
            };
        }
    }
    
    /// <summary>
    /// 鸣牌组信息
    /// </summary>
    public class MeldGroup
    {
        public List<TileTexture> Tiles { get; set; } = new List<TileTexture>();
        public MeldType Type { get; set; }
        public DateTime Timestamp { get; set; }
        
        public MeldGroup(List<TileTexture> tiles)
        {
            Tiles = tiles;
            Type = DetermineMeldType(tiles);
            Timestamp = DateTime.Now;
        }
        
        private MeldType DetermineMeldType(List<TileTexture> tiles)
        {
            if (tiles.Count < 3) return MeldType.Unknown;
            
            // 使用标准化的标记（将红宝牌转换为对应的普通5）进行排序分析
            var normalizedNotations = tiles.Select(t => NormalizeTileNotation(t.MjaiNotation)).OrderBy(n => n).ToList();
            
            // 杠 - 4张等价的牌
            if (tiles.Count == 4 && normalizedNotations.All(n => n == normalizedNotations[0]))
            {
                return MeldType.Kan;
            }
            
            // 碰 - 3张等价的牌
            if (tiles.Count == 3 && normalizedNotations.All(n => n == normalizedNotations[0]))
            {
                return MeldType.Pon;
            }
            
            // 吃 - 3张连续的数字牌
            if (tiles.Count == 3)
            {
                // 检查是否为同花色的连续数字
                var suit = normalizedNotations[0].Substring(1);
                if (normalizedNotations.All(n => n.Substring(1) == suit) && suit != "z")
                {
                    var numbers = normalizedNotations.Select(n => GetTileNumber(n)).OrderBy(n => n).ToArray();
                    if (numbers[1] == numbers[0] + 1 && numbers[2] == numbers[1] + 1)
                    {
                        return MeldType.Chi;
                    }
                }
            }
            
            return MeldType.Unknown;
        }
        
        public override bool Equals(object? obj)
        {
            if (obj is not MeldGroup other) return false;
            
            if (Tiles.Count != other.Tiles.Count) return false;
            
            // 使用标准化的标记进行比较，考虑红宝牌等价性
            var myNotations = Tiles.Select(t => NormalizeTileNotation(t.MjaiNotation)).OrderBy(n => n).ToList();
            var otherNotations = other.Tiles.Select(t => NormalizeTileNotation(t.MjaiNotation)).OrderBy(n => n).ToList();
            
            return myNotations.SequenceEqual(otherNotations);
        }
        
        public override int GetHashCode()
        {
            // 使用标准化的标记计算哈希值，考虑红宝牌等价性
            var normalizedNotations = Tiles.Select(t => NormalizeTileNotation(t.MjaiNotation)).OrderBy(n => n);
            return string.Join(",", normalizedNotations).GetHashCode();
        }
    }
    
    /// <summary>
    /// 鸣牌类型
    /// </summary>
    public enum MeldType
    {
        Unknown,
        Chi,    // 吃
        Pon,    // 碰
        Kan,    // 杠（明杠）
        AnKan,  // 暗杠
        KaKan   // 加杠
    }
    
    /// <summary>
    /// 鸣牌事件
    /// </summary>
    public class MeldEvent
    {
        public MahjongNodeType Player { get; set; }
        public MeldGroup MeldGroup { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsNewMeld { get; set; } // 是否为新鸣牌（而非加杠等）
        
        public MeldEvent(MahjongNodeType player, MeldGroup meldGroup, bool isNewMeld = true)
        {
            Player = player;
            MeldGroup = meldGroup;
            Timestamp = DateTime.Now;
            IsNewMeld = isNewMeld;
        }
        
        public string GetPlayerName()
        {
            return Player switch
            {
                MahjongNodeType.PLAYER_MELD_GROUP => "自家",
                MahjongNodeType.RIGHT_MELD_GROUP => "下家",
                MahjongNodeType.FAR_MELD_GROUP => "对家",
                MahjongNodeType.LEFT_MELD_GROUP => "上家",
                _ => "未知"
            };
        }
        
        public string GetMeldTypeName()
        {
            return MeldGroup.Type switch
            {
                MeldType.Chi => "吃",
                MeldType.Pon => "碰", 
                MeldType.Kan => "杠",
                _ => "未知"
            };
        }
        
        public override string ToString()
        {
            var tiles = string.Join(",", MeldGroup.Tiles.Select(t => t.MjaiNotation));
            return $"{GetPlayerName()} {GetMeldTypeName()}: {tiles}";
        }
    }
    
    /// <summary>
    /// 检测鸣牌事件
    /// </summary>
    private void DetectMeldEvents()
    {
        var currentMeldGroups = GetCurrentMeldGroups();
        
        foreach (var playerType in currentMeldGroups.Keys)
        {
            var currentMelds = currentMeldGroups[playerType];
            var previousMelds = PreviousMeldGroups.GetValueOrDefault(playerType, new List<MeldGroup>());
            
            // 检测新增的鸣牌组
            var newMelds = currentMelds.Where(current => !previousMelds.Any(prev => prev.Equals(current))).ToList();
            
            foreach (var newMeld in newMelds)
            {
                var meldEvent = new MeldEvent(playerType, newMeld, true);
                RecentMeldEvents.Add(meldEvent);
                
                // 触发事件
                OnMeldEvent?.Invoke(meldEvent);
                
                DService.Log.Info($"检测到鸣牌事件: {meldEvent}");
            }
        }
        
        // 更新历史记录
        PreviousMeldGroups = currentMeldGroups;
        
        // 只保留最近的10个事件
        if (RecentMeldEvents.Count > 10)
        {
            RecentMeldEvents = RecentMeldEvents.Skip(RecentMeldEvents.Count - 10).ToList();
        }
    }
    
    /// <summary>
    /// 检测鸣牌提示
    /// </summary>
    private void DetectMeldSuggestions()
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 获取当前手牌
        var currentHand = GetPlayerHandTiles();
        if (currentHand.Count == 0) return;
        
        // 检测最新弃牌
        DetectLatestDiscard();
        
        // 检测可以对别人弃牌的操作
        if (LastDiscardedTile != null)
        {
            var chiSuggestions = CheckChiSuggestions(currentHand, LastDiscardedTile, LastDiscardPlayer);
            var ponSuggestions = CheckPonSuggestions(currentHand, LastDiscardedTile, LastDiscardPlayer);
            var kanSuggestions = CheckKanSuggestions(currentHand, LastDiscardedTile, LastDiscardPlayer);
            
            suggestions.AddRange(chiSuggestions);
            suggestions.AddRange(ponSuggestions);
            suggestions.AddRange(kanSuggestions);
        }
        
        // 检测暗杠和加杠
        suggestions.AddRange(CheckAnKanSuggestions(currentHand));
        suggestions.AddRange(CheckKaKanSuggestions(currentHand));
        
        // 更新提示
        CurrentMeldSuggestions = suggestions;
        
        // 触发事件
        OnMeldSuggestionsUpdated?.Invoke(suggestions);
        
        if (suggestions.Count > 0)
        {
            DService.Log.Info($"检测到 {suggestions.Count} 个鸣牌提示");
        }
    }
    
    /// <summary>
    /// 获取玩家手牌
    /// </summary>
    private List<TileTexture> GetPlayerHandTiles()
    {
        return ObservedTiles
            .Where(t => t.PlayerArea == MahjongNodeType.PLAYER_HAND_TILE)
            .Select(t => t.TileTexture)
            .ToList();
    }
    
    /// <summary>
    /// 检测最新弃牌（基于河牌状态变化）
    /// </summary>
    private void DetectLatestDiscard()
    {
        // 获取当前各玩家的河牌状态
        var currentDiscardPiles = GetCurrentDiscardPiles();
        
        DService.Log.Info($"开始检测弃牌，检查 {currentDiscardPiles.Count} 个玩家的河牌");
        
        // 检查每个玩家的河牌是否有新增
        foreach (var kvp in currentDiscardPiles)
        {
            var playerType = kvp.Key;
            var currentPile = kvp.Value;
            var previousPile = PreviousDiscardPiles.GetValueOrDefault(playerType, new List<string>());
            
            DService.Log.Info($"{playerType}: 当前河牌数量 {currentPile.Count}, 之前数量 {previousPile.Count}");
            
            // 如果当前河牌比之前多，说明有新弃牌
            if (currentPile.Count > previousPile.Count)
            {
                var newTileNotation = currentPile.First(); // 第一张牌是最新的
                
                // 只检测其他玩家的弃牌（不包括自己，因为不能对自己的弃牌鸣牌）
                if (playerType != MahjongNodeType.PLAYER_DISCARD_TILE)
                {
                    // 从观察到的牌中找到对应的TileTexture
                    var newTile = ObservedTiles
                        .Where(t => t.PlayerArea == playerType && t.TileTexture.MjaiNotation == newTileNotation)
                        .FirstOrDefault()?.TileTexture;
                    
                    if (newTile != null)
                    {
                        LastDiscardedTile = newTile;
                        LastDiscardPlayer = playerType;
                        
                        DService.Log.Info($"检测到新弃牌: {GetPlayerName(playerType)} 打出 {newTileNotation}");
                        
                        // 更新历史记录
                        PreviousDiscardPiles[playerType] = new List<string>(currentPile);
                        return; // 找到新弃牌就立即返回
                    }
                }
                else
                {
                    DService.Log.Info($"跳过自家弃牌: {newTileNotation}");
                    // 更新历史记录但不触发鸣牌检测
                    PreviousDiscardPiles[playerType] = new List<string>(currentPile);
                }
            }
            else if (currentPile.Count == previousPile.Count)
            {
                DService.Log.Info($"{playerType}: 河牌数量未变化");
            }
            else
            {
                DService.Log.Info($"{playerType}: 河牌数量减少（可能是新局开始），重置状态");
                // 河牌数量减少，可能是新局开始，重置状态
                PreviousDiscardPiles[playerType] = new List<string>(currentPile);
            }
        }
        
        // 更新所有历史记录
        foreach (var kvp in currentDiscardPiles)
        {
            PreviousDiscardPiles[kvp.Key] = new List<string>(kvp.Value);
        }
        
        DService.Log.Info("未检测到新弃牌");
    }
    
    /// <summary>
    /// 获取当前各玩家河牌状态
    /// </summary>
    private Dictionary<MahjongNodeType, List<string>> GetCurrentDiscardPiles()
    {
        var discardPiles = new Dictionary<MahjongNodeType, List<string>>();
        
        // 各个玩家的弃牌区域
        var playerTypes = new[]
        {
            MahjongNodeType.PLAYER_DISCARD_TILE,
            MahjongNodeType.RIGHT_DISCARD_TILE,
            MahjongNodeType.FAR_DISCARD_TILE,
            MahjongNodeType.LEFT_DISCARD_TILE
        };
        
        foreach (var playerType in playerTypes)
        {
            var playerDiscards = ObservedTiles
                .Where(t => t.PlayerArea == playerType)
                .Select(t => t.TileTexture.MjaiNotation)
                .ToList();
            
            discardPiles[playerType] = playerDiscards;
        }
        
        return discardPiles;
    }
    
    /// <summary>
    /// 获取玩家名称
    /// </summary>
    private string GetPlayerName(MahjongNodeType playerType)
    {
        return playerType switch
        {
            MahjongNodeType.PLAYER_DISCARD_TILE => "自家",
            MahjongNodeType.RIGHT_DISCARD_TILE => "下家",
            MahjongNodeType.FAR_DISCARD_TILE => "对家",
            MahjongNodeType.LEFT_DISCARD_TILE => "上家",
            _ => "未知"
        };
    }
    
    /// <summary>
    /// 检查吃的可能性
    /// </summary>
    private List<MeldSuggestion> CheckChiSuggestions(List<TileTexture> hand, TileTexture targetTile, MahjongNodeType sourcePlayer)
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 只能吃左家（上一家）的牌
        if (sourcePlayer != MahjongNodeType.LEFT_DISCARD_TILE) return suggestions;
        
        // 字牌不能吃
        if (targetTile.MjaiNotation.EndsWith("z")) return suggestions;
        
        var targetNumber = GetTileNumber(targetTile.MjaiNotation);
        var targetSuit = GetTileSuit(targetTile.MjaiNotation);
        
        // 检查三种吃的方式，使用红宝牌等价性检查
        // 1. a,b,target (target是最大的)
        if (targetNumber >= 3)
        {
            var tile1 = FindEquivalentTiles(hand, targetNumber - 2, targetSuit);
            var tile2 = FindEquivalentTiles(hand, targetNumber - 1, targetSuit);
            
            if (tile1.Count > 0 && tile2.Count > 0)
            {
                var requiredTiles = new List<TileTexture> { tile1.First(), tile2.First() };
                suggestions.Add(new MeldSuggestion(MeldType.Chi, requiredTiles, targetTile, sourcePlayer));
            }
        }
        
        // 2. a,target,c (target是中间的)
        if (targetNumber >= 2 && targetNumber <= 8)
        {
            var tile1 = FindEquivalentTiles(hand, targetNumber - 1, targetSuit);
            var tile2 = FindEquivalentTiles(hand, targetNumber + 1, targetSuit);
            
            if (tile1.Count > 0 && tile2.Count > 0)
            {
                var requiredTiles = new List<TileTexture> { tile1.First(), tile2.First() };
                suggestions.Add(new MeldSuggestion(MeldType.Chi, requiredTiles, targetTile, sourcePlayer));
            }
        }
        
        // 3. target,b,c (target是最小的)
        if (targetNumber <= 7)
        {
            var tile1 = FindEquivalentTiles(hand, targetNumber + 1, targetSuit);
            var tile2 = FindEquivalentTiles(hand, targetNumber + 2, targetSuit);
            
            if (tile1.Count > 0 && tile2.Count > 0)
            {
                var requiredTiles = new List<TileTexture> { tile1.First(), tile2.First() };
                suggestions.Add(new MeldSuggestion(MeldType.Chi, requiredTiles, targetTile, sourcePlayer));
            }
        }
        
        return suggestions;
    }
    
    /// <summary>
    /// 检查碰的可能性
    /// </summary>
    private List<MeldSuggestion> CheckPonSuggestions(List<TileTexture> hand, TileTexture targetTile, MahjongNodeType sourcePlayer)
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 检查手牌中是否有至少2张等价的牌（考虑红宝牌）
        var equivalentTiles = hand.Where(t => AreTilesEquivalent(t.MjaiNotation, targetTile.MjaiNotation)).ToList();
        
        if (equivalentTiles.Count >= 2)
        {
            var requiredTiles = equivalentTiles.Take(2).ToList();
            suggestions.Add(new MeldSuggestion(MeldType.Pon, requiredTiles, targetTile, sourcePlayer));
        }
        
        return suggestions;
    }
    
    /// <summary>
    /// 检查明杠的可能性
    /// </summary>
    private List<MeldSuggestion> CheckKanSuggestions(List<TileTexture> hand, TileTexture targetTile, MahjongNodeType sourcePlayer)
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 检查手牌中是否有3张等价的牌（考虑红宝牌）
        var equivalentTiles = hand.Where(t => AreTilesEquivalent(t.MjaiNotation, targetTile.MjaiNotation)).ToList();
        
        if (equivalentTiles.Count >= 3)
        {
            var requiredTiles = equivalentTiles.Take(3).ToList();
            suggestions.Add(new MeldSuggestion(MeldType.Kan, requiredTiles, targetTile, sourcePlayer));
        }
        
        return suggestions;
    }
    
    /// <summary>
    /// 检查暗杠的可能性
    /// </summary>
    private List<MeldSuggestion> CheckAnKanSuggestions(List<TileTexture> hand)
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 按等价性分组统计每张牌的数量（考虑红宝牌）
        var equivalentGroups = hand.GroupBy(t => NormalizeTileNotation(t.MjaiNotation)).Where(g => g.Count() >= 4);
        
        foreach (var group in equivalentGroups)
        {
            var tiles = group.Take(4).ToList();
            var targetTile = tiles.First();
            suggestions.Add(new MeldSuggestion(MeldType.AnKan, tiles, targetTile, MahjongNodeType.PLAYER_HAND_TILE));
        }
        
        return suggestions;
    }
    
    /// <summary>
    /// 检查加杠的可能性
    /// </summary>
    private List<MeldSuggestion> CheckKaKanSuggestions(List<TileTexture> hand)
    {
        var suggestions = new List<MeldSuggestion>();
        
        // 获取已有的碰牌组
        var playerMeldGroups = GetMeldGroupsFromPointers(ImportantPointers.PlayerMeldGroups, MahjongNodeType.PLAYER_MELD_GROUP, true);
        var ponGroups = playerMeldGroups.Where(m => m.Type == MeldType.Pon).ToList();
        
        foreach (var ponGroup in ponGroups)
        {
            var ponTileNotation = ponGroup.Tiles.First().MjaiNotation;
            
            // 查找手牌中等价的牌（考虑红宝牌）
            var matchingHandTiles = hand.Where(t => AreTilesEquivalent(t.MjaiNotation, ponTileNotation)).ToList();
            
            if (matchingHandTiles.Count >= 1)
            {
                var requiredTiles = new List<TileTexture> { matchingHandTiles.First() };
                suggestions.Add(new MeldSuggestion(MeldType.KaKan, requiredTiles, matchingHandTiles.First(), MahjongNodeType.PLAYER_HAND_TILE));
            }
        }
        
        return suggestions;
    }
    
    /// <summary>
    /// 获取当前所有鸣牌组
    /// </summary>
    private Dictionary<MahjongNodeType, List<MeldGroup>> GetCurrentMeldGroups()
    {
        var meldGroups = new Dictionary<MahjongNodeType, List<MeldGroup>>();
        
        // 获取各个玩家的鸣牌组
        var playerMelds = GetMeldGroupsFromPointers(ImportantPointers.PlayerMeldGroups, MahjongNodeType.PLAYER_MELD_GROUP, true);
        var rightMelds = GetMeldGroupsFromPointers(ImportantPointers.RightMeldGroups, MahjongNodeType.RIGHT_MELD_GROUP, false);
        var farMelds = GetMeldGroupsFromPointers(ImportantPointers.FarMeldGroups, MahjongNodeType.FAR_MELD_GROUP, false);
        var leftMelds = GetMeldGroupsFromPointers(ImportantPointers.LeftMeldGroups, MahjongNodeType.LEFT_MELD_GROUP, false);
        
        if (playerMelds.Count > 0) meldGroups[MahjongNodeType.PLAYER_MELD_GROUP] = playerMelds;
        if (rightMelds.Count > 0) meldGroups[MahjongNodeType.RIGHT_MELD_GROUP] = rightMelds;
        if (farMelds.Count > 0) meldGroups[MahjongNodeType.FAR_MELD_GROUP] = farMelds;
        if (leftMelds.Count > 0) meldGroups[MahjongNodeType.LEFT_MELD_GROUP] = leftMelds;
        
        return meldGroups;
    }
    
    /// <summary>
    /// 从指针列表获取鸣牌组
    /// </summary>
    private unsafe List<MeldGroup> GetMeldGroupsFromPointers(List<IntPtr> ptrs, MahjongNodeType playerType, bool isPlayer)
    {
        var meldGroups = new List<MeldGroup>();
        
        foreach (var ptr in ptrs)
        {
            List<TileTexture>? tileTextures = null;
            
            if (isPlayer)
            {
                tileTextures = NodeCrawlerUtils.GetTileTexturesFromPlayerMeldGroup(ptr);
            }
            else
            {
                tileTextures = NodeCrawlerUtils.GetTileTexturesFromMeldGroup(ptr);
            }
            
            if (tileTextures != null && tileTextures.Count >= 3)
            {
                var meldGroup = new MeldGroup(tileTextures);
                meldGroups.Add(meldGroup);
            }
        }
        
        return meldGroups;
    }
    
    #endregion

    #region 辅助方法

    /// <summary>
    /// 检查两张牌是否等价（考虑红宝牌）
    /// </summary>
    private bool AreTilesEquivalent(string notation1, string notation2)
    {
        if (notation1 == notation2) return true;
        
        // 检查红宝牌与对应的普通5的等价性
        var normalizedNotation1 = NormalizeTileNotation(notation1);
        var normalizedNotation2 = NormalizeTileNotation(notation2);
        
        return normalizedNotation1 == normalizedNotation2;
    }
    
    /// <summary>
    /// 将红宝牌标记转换为对应的普通5
    /// </summary>
    private static string NormalizeTileNotation(string notation)
    {
        return notation switch
        {
            "0s" => "5s", // 红5索
            "0m" => "5m", // 红5万
            "0p" => "5p", // 红5筒
            _ => notation
        };
    }
    
    /// <summary>
    /// 获取牌的数字值（考虑红宝牌）
    /// </summary>
    private static int GetTileNumber(string notation)
    {
        if (notation.StartsWith("0"))
        {
            return 5; // 红宝牌都是5
        }
        return int.Parse(notation.Substring(0, 1));
    }
    
    /// <summary>
    /// 获取牌的花色
    /// </summary>
    private string GetTileSuit(string notation)
    {
        return notation.Substring(1);
    }
    
    /// <summary>
    /// 检查手牌中是否有指定数字和花色的牌（考虑红宝牌等价性）
    /// </summary>
    private List<TileTexture> FindEquivalentTiles(List<TileTexture> hand, int number, string suit)
    {
        return hand.Where(t => 
        {
            var handSuit = GetTileSuit(t.MjaiNotation);
            var handNumber = GetTileNumber(t.MjaiNotation);
            return handSuit == suit && handNumber == number;
        }).ToList();
    }

    private void ResetMeldEvents()
    {
        PreviousMeldGroups.Clear();
        RecentMeldEvents.Clear();
        CurrentMeldSuggestions.Clear();
        PreviousDiscardPiles.Clear();
        LastDiscardedTile = null;
        LastDiscardPlayer = MahjongNodeType.PLAYER_DISCARD_TILE;
    }

    #endregion
}
