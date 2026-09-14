using System;
using System.IO;
using System.Linq;
namespace Editor;

internal sealed class AudioEventLibraryWidget : Widget, AssetSystem.IEventListener
{
 private readonly SearchWidget search;
 private readonly Label count;
 private readonly EventList list;
 private Asset[] events = [];
 internal int EventCount => events.Length;
 internal int MatchCount {get;private set;}
 public AudioEventLibraryWidget(Widget parent) : base(parent)
 {
  WindowTitle="Audio Events";MinimumWidth=240;
  Layout=Layout.Column();Layout.Margin=6;Layout.Spacing=6;
  var header=Layout.AddRow();header.Add(new Label("Project Audio Events"),1);
  count=header.Add(new Label());
  var refresh=header.Add(new ToolButton(string.Empty,"refresh",this));refresh.MouseLeftPress=RefreshAssets;
  search=Layout.Add(new SearchWidget(this,false));search.PlaceholderText="Search event name or path";search.ValueChanged+=ApplySearch;
  list=Layout.Add(new EventList(this),1);
  Layout.Add(new Label("Double-click an event to open it.") {WordWrap=true});
  RefreshAssets();
 }
 private void RefreshAssets()
 {
  var root=Path.GetFullPath(Project.Current.GetAssetsPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
  var next=AssetSystem.All.Where(a=>a is not null && a.RelativePath.EndsWith(".audevt",StringComparison.OrdinalIgnoreCase)
   && Path.GetFullPath(a.AbsolutePath).StartsWith(root,StringComparison.OrdinalIgnoreCase))
   .OrderBy(a=>a.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
  if(events.SequenceEqual(next))return;
  events=next;ApplySearch();
 }
 internal void SetSearch(string query){search.Value=query;ApplySearch();}
 private void ApplySearch()
 {
  var query=search.Value?.Trim() ?? "";
  var matches=events.Where(a=>a.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||a.RelativePath.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
  MatchCount=matches.Length;count.Text=$"{matches.Length}/{events.Length}";list.SetItems(matches.Cast<object>());
 }
 void AssetSystem.IEventListener.OnAssetSystemChanges()=>RefreshAssets();
 void AssetSystem.IEventListener.OnAssetTagsChanged(){}
 private sealed class EventList : ListView
 {
  public EventList(Widget parent):base(parent)
  {
   ItemSize=new Vector2(0,44);ItemSpacing=2;MultiSelect=false;
   ItemActivated=value=>{if(value is Asset asset)AudioEventEditorWindow.OpenChildEvent(asset.RelativePath);};
  }
  protected override void PaintItem(VirtualWidget widget)
  {
   if(widget.Object is not Asset asset)return;
   Paint.ClearPen();Paint.SetBrush((Paint.HasSelected||Paint.HasMouseOver ? Theme.Primary : Theme.ControlBackground).WithAlpha(.25f));Paint.DrawRect(widget.Rect);
   var rect=widget.Rect.Shrink(7,3);
   Paint.SetPen(Theme.Text);Paint.SetDefaultFont(9,600);Paint.DrawText(new Rect(rect.Left,rect.Top,rect.Width,18),asset.Name,TextFlag.LeftCenter|TextFlag.SingleLine);
   Paint.SetPen(Theme.TextLight);Paint.SetDefaultFont(7);Paint.DrawText(new Rect(rect.Left,rect.Top+19,rect.Width,16),asset.RelativePath,TextFlag.LeftCenter|TextFlag.SingleLine);
  }
 }
}
