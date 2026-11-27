using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private readonly CheckBox chain = Check("使用前置代理 → 最终出口两跳连接");
    private readonly ComboBox upstreamSource = Choice(["已有订阅链接", "节点链接 / 本地配置"]), upstreamNode = Choice([]);
    private readonly TextBox upstreamSubscription = Input(), upstreamText = Input("", true);
    private string upstreamImported = "";
    private bool currentFinalSubscription, currentUpstreamSubscription;

    private void BuildChainPage()
    {
        upstreamSource.SelectedIndex=1;upstreamText.Height=100;
        var body=new StackPanel();
        body.Children.Add(Field("前置节点来源",upstreamSource));
        var link=Field("前置订阅 HTTPS 链接",upstreamSubscription);
        var config=Field("前置节点链接 / Clash 配置",upstreamText);
        body.Children.Add(link);body.Children.Add(config);
        body.Children.Add(Row(Action("读取前置节点",async(_,_)=> {
            if(busy)return;
            try {
                busy=true;AllowEditing(false);
                var text=upstreamSource.SelectedIndex==0?await NetworkImport.Fetch(upstreamSubscription.Text.Trim(),CancellationToken.None):upstreamText.Text;
                ReplaceUpstreamDraft(NetworkImport.Parse(text));
                Notice("请选择前置节点。最终出口仍使用上方选择的节点，地区和时区按最终出口填写。");
            }catch(Exception ex){Notice(Redaction.SafeError(ex));}finally{busy=false;AllowEditing(true);}
        })));
        body.Children.Add(Field("明确选择前置节点",upstreamNode));
        body.Children.Add(Text("支持两跳 dialer-proxy。导入文件若直接引用了一个前置节点，会自动保留这条链；引用动态策略组或更多跳数时会要求拆开选择。不会自动换节点。",11));
        var expander=new Expander{Header="链式代理（普通代理作为前置，住宅节点作为最终出口）",Content=Card(chain,body),Margin=new Thickness(0,0,5,16)};
        pages[3].Children.Add(expander);
        void Update() { if(chain.IsChecked==true)expander.IsExpanded=true;body.Visibility=chain.IsChecked==true?Visibility.Visible:Visibility.Collapsed;link.Visibility=upstreamSource.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;config.Visibility=upstreamSource.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed; }
        chain.Checked += (_, _) => Update();
        chain.Unchecked += (_, _) => Update();
        upstreamSource.SelectionChanged += (_, _) =>
        {
            upstreamNode.ItemsSource = null;
            upstreamImported = "";
            currentUpstreamSubscription = false;
            Update();
        };
        source.SelectionChanged+=(_,_)=>expander.Visibility=source.SelectedIndex==3?Visibility.Collapsed:Visibility.Visible;
        expander.Visibility=source.SelectedIndex==3?Visibility.Collapsed:Visibility.Visible;
        Update();
    }
    private string UpstreamFingerprint()=>upstreamSource.SelectedIndex==0?upstreamSubscription.Text.Trim():upstreamText.Text;
    private Secrets ChainSecrets(Secrets secrets)
    {
        if(source.SelectedIndex==3)return secrets;
        var upstream=chain.IsChecked==true?upstreamNode.SelectedItem as ProxyNode:(node.SelectedItem as ProxyNode)?.Upstream;
        secrets.UpstreamNodeJson=upstream?.Json??"";
        secrets.UpstreamSubscriptionUrl=upstreamSubscription.Text.Trim();secrets.UpstreamNetworkText=upstreamText.Text;
        return secrets;
    }
    private void ClearChainInputs()
    {
        chain.IsChecked=false;upstreamSubscription.Clear();upstreamText.Clear();upstreamNode.ItemsSource=null;upstreamImported="";
        currentFinalSubscription = currentUpstreamSubscription = false;
    }
    private void LoadChainSecrets(Secrets saved)
    {
        upstreamSubscription.Text=saved.UpstreamSubscriptionUrl;upstreamText.Text=saved.UpstreamNetworkText;
        if(!string.IsNullOrEmpty(saved.UpstreamNodeJson)) {
            var parsed=NetworkImport.Parse("{\"proxies\":["+saved.UpstreamNodeJson+"]}");
            upstreamNode.ItemsSource=parsed.Nodes;upstreamNode.SelectedIndex=0;upstreamImported=UpstreamFingerprint();
        }
    }
    private void RestoreChainRelationship(Secrets saved,SetupOptions options)
    {
        if(options.ChainFromImport && node.SelectedItem is ProxyNode final && upstreamNode.SelectedItem is ProxyNode first) {
            nodes=[final with {Upstream=first}];node.ItemsSource=nodes;node.SelectedIndex=0;
        }
    }
    private void ReplaceFinalDraft(NodeList imported)
    {
        lastImportIssues = imported.Issues;
        nodes = imported.Nodes;
        node.ItemsSource = nodes;
        node.SelectedIndex = -1;
        currentFinalSubscription = source.SelectedIndex == 0;
        importedInput = currentFinalSubscription ? subscription.Text.Trim() : networkText.Text;
    }

    private void ReplaceUpstreamDraft(NodeList imported)
    {
        upstreamNode.ItemsSource = imported.Nodes;
        upstreamNode.SelectedIndex = -1;
        upstreamImported = UpstreamFingerprint();
        currentUpstreamSubscription = upstreamSource.SelectedIndex == 0;
    }

    private async Task RefreshSelectedRoutes(SetupOptions options, Secrets secrets, CancellationToken token,
        Func<string, CancellationToken, Task<string>>? fetch = null)
    {
        if (options.NetworkSource == "稍后配置") return;
        fetch ??= NetworkImport.Fetch;
        var selected = node.SelectedItem as ProxyNode ?? throw new SetupException("请明确选择最终出口节点。");
        // Applying an explicit selection uses the list the user has already loaded.
        // Only a selection restored from a saved task needs a new subscription read.
        if (options.NetworkSource == "已有订阅链接" && !currentFinalSubscription)
        {
            var imported = NetworkImport.Parse(await fetch(secrets.SubscriptionUrl, token));
            if (!imported.Nodes.Any(n => NetworkImport.SameDefinition(n, selected) &&
                NetworkImport.SameDefinition(n.Upstream, selected.Upstream)))
            {
                var stillNamed = imported.Nodes.Any(n => n.Name == selected.Name);
                ReplaceFinalDraft(imported);
                throw new SetupException(stillNamed
                    ? "订阅里有同名节点，但线路参数或前置节点已变化。列表已更新，请重新选择后再应用。"
                    : "订阅里已没有这条线路。列表已更新，请选择新的线路后再应用。");
            }
        }
        secrets.SelectedNodeJson = selected.Json;
        if (!options.ChainEnabled)
        {
            secrets.UpstreamNodeJson = "";
            return;
        }
        var first = selected.Upstream;
        if (chain.IsChecked == true)
        {
            if (upstreamImported != UpstreamFingerprint()) throw new SetupException("前置资料已改变，请重新读取并选择。");
            first = upstreamNode.SelectedItem as ProxyNode ?? throw new SetupException("请选择前置节点。");
            if (upstreamSource.SelectedIndex == 0 && !currentUpstreamSubscription)
            {
                var imported = NetworkImport.Parse(await fetch(secrets.UpstreamSubscriptionUrl, token));
                if (!imported.Nodes.Any(n => n.Upstream is null && NetworkImport.SameDefinition(n, first)))
                {
                    ReplaceUpstreamDraft(imported);
                    throw new SetupException("前置订阅的节点已变化或下架。列表已更新，请重新选择前置节点后再应用。");
                }
            }
        }
        if (first is null || first.Upstream is not null) throw new SetupException("前置必须是单个独立节点；当前仅支持两跳。");
        secrets.UpstreamNodeJson = first.Json;
    }
    private Border BuildValidationCard()=>Card(Text("检查已保存的配置",16,false),Text("自动模式会回读原 AdsPower 专用环境。手动模式只核对 Clash 与来宾网络，浏览器检查在虚拟机内完成。运行检查保留现有代理与浏览器配置。",11),Row(
        Action("检查已配置环境",async(_,_)=>await RunValidation(false,false)),
        Action("断线与恢复检查",async(_,_)=> {
            if(MessageBox.Show(this,"检查会短暂停止来宾代理，并检查桌面请求和专用浏览器是否断网，再恢复。请先保存在线工作。继续？","断线检查",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)await RunValidation(false,true);
        })),Row(Action("重启虚拟机并检查",async(_,_)=> {
            if(MessageBox.Show(this,"请先保存虚拟机中的工作。将请求正常重启这台任务虚拟机；重新登录 Windows 后检查持久配置和实际网络，检查前不会修复配置。保持助手开启。继续？","重启验收",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)await RunValidation(true,false);
        })));
    private async Task RunValidation(bool restart, bool outageTest)
    {
        var showResult = false;
        var routeBActive = state.Options.PlatformId == "hyperv" &&
            !explicitHyperVFallbackToA &&
            (hyperVDesktopMode is HyperVDesktopMode.EmbeddedRdp or HyperVDesktopMode.StandaloneRdp ||
             hyperVRdpHost?.IsConnected == true ||
             standaloneRdpWindow is not null ||
             ProductProfile.IsHyperVStandalone);
        await Perform(async (_, token) =>
        {
            if (!restart && !outageTest)
                await Engine().ReadProtection(state, AppliedLogin(), token);
            else
                await Engine().ValidateExisting(state, AppliedLogin(), restart, outageTest, token, routeBConsoleActive: routeBActive);
            showResult = true;
        }, useSavedOptions: true);
        if (restart && routeBActive && !closeAfterPause && hyperVRdpHost?.IsConnected != true && standaloneRdpWindow is null)
            _ = ConnectDesktop(true);
        if (showResult && !closeAfterPause) Notice(ProtectionSummary());
    }
}
