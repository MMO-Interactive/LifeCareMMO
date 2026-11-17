using Engine.GUI;
using StormChase.Protocol;

internal sealed partial class StormChaseHud
{
    public HousingState? Housing {get;set;}
    public bool HousingOpen {get;set;}
    public long HousingCashCents {get;set;}
    public int HousingSlot {get;set;}
    public int HousingPage {get;set;}
    public int HousingSelectedFurniture {get;set;}
    public int HousingInventoryPage {get;set;}
    public float HousingPlacementYaw {get;set;}
    private GuiDocument BuildHousingDocument(int width,int height)
    {
        float wide=Math.Min(850,width-24),tall=Math.Min(708,height-24),x=(width-wide)*.5f,y=(height-tall)*.5f;
        var ink=new GuiColor(232,240,235);var muted=new GuiColor(159,180,184);var accent=new GuiColor(87,225,190);var elements=new List<GuiElement>();
        void Text(string text,float top,float size=11,GuiColor? color=null,float space=28)=>elements.Add(new GuiLabel{Text=text,Translation=new(22,top),Width=GuiLength.Pixels(wide-44),Height=GuiLength.Pixels(space),Style=new(Theme.Font,size,1.1f,color??ink),WrapMode=GuiTextWrapMode.Wrap});
        void Button(string id,string text,float left,float top,float length,bool enabled=true)=>elements.Add(new GuiButton{Id=id,Text=text,Translation=new(left,top),Width=GuiLength.Pixels(length),Height=GuiLength.Pixels(33),IsEnabled=enabled,TextStyle=new(Theme.Font,10,1.1f,ink),BackgroundColor=new(20,45,52),BorderColor=new(64,104,111),HoverBackgroundColor=new(31,78,81),PressedBackgroundColor=new(39,108,99),CornerRadius=5});
        Text("YOUR HOME & CHASER HANGOUTS",18,22,accent,40);Text($"${HousingCashCents/100m:0}  ·  Furnishings stay with your character  ·  P / ESC returns to chase",60,11,muted);
        float third=(wide-52)/3;
        Button("housing/page/0","AVAILABLE HOMES",22,99,third);Button("housing/page/1","FURNISH MY HOME",26+third,99,third);Button("housing/page/2","VISIT CHASERS",30+third*2,99,third);
        var state=Housing;
        if(state is null)Text("Waiting for the server's property listings…",160,13,muted);
        else if(HousingPage==0)
        {
            Text(state.Home is {} mine?$"OWNED: {mine.Name}  ·  {(mine.Destroyed?"STORM DAMAGE":"Front door open")}":"Buy at the front entrance. Earn money through field contracts and storm research.",147,12,accent,42);
            var homes=state.Homes.Where(h=>h.OwnerCharacterId==""||h.Mine).Take(8).ToArray();float row=Math.Clamp((tall-308)/8,31,47);
            for(int i=0;i<homes.Length;i++)
            {
                var h=homes[i];float top=203+i*row;
                float range=MathF.Sqrt((h.Entrance.X-observerPosition.X)*(h.Entrance.X-observerPosition.X)+(h.Entrance.Z-observerPosition.Z)*(h.Entrance.Z-observerPosition.Z))/1000;
                Button("housing/locate/"+h.HomeId,h.Name+$"  ·  {range:0.0} KM",22,top,wide*.58f);
                Button("housing/buy-home/"+h.HomeId,h.Mine?"OWNED":h.Destroyed?"STORM DAMAGE":$"BUY  ${h.PriceCents/100m:0}",wide*.62f,top,wide*.38f-22,!h.Mine&&!h.Destroyed&&state.Home is null&&HousingCashCents>=h.PriceCents);
            }
        }
        else if(HousingPage==1)
        {
            Text(state.Home is {} home?$"{home.Name}  ·  {state.Furniture.Count(f=>f.OwnerCharacterId==home.OwnerCharacterId)}/{state.MaxFurniture} items":"Purchase a home first. Your field-work income pays for furnishings.",146,12,accent);
            Text("Buy furniture, enter your house, choose an item and an open slot, then place it.",176,11,muted);
            float leftWidth=(wide-60)*.5f;int offerCount=Math.Min(7,state.Offers.Length);
            for(int i=0;i<offerCount;i++)
            {
                var offer=state.Offers[i];Button("housing/buy-furniture/"+offer.Id,$"{offer.Name}  ${offer.PriceCents/100m:0}",22,216+i*38,leftWidth,state.Home is not null&&HousingCashCents>=offer.PriceCents&&state.Furniture.Count(f=>f.OwnerCharacterId==state.Home.OwnerCharacterId)<state.MaxFurniture);
            }
            var owned=state.Home is null?[]:state.Furniture.Where(f=>f.OwnerCharacterId==state.Home.OwnerCharacterId).ToArray();
            int start=Math.Clamp(HousingInventoryPage*6,0,Math.Max(0,(owned.Length-1)/6)*6);
            for(int i=0;i<Math.Min(6,owned.Length-start);i++)
            {
                var item=owned[start+i];string name=state.Offers.FirstOrDefault(o=>o.Sku==item.Sku)?.Name??item.Sku;
                Button("housing/select/"+item.FurnitureId,$"{(item.FurnitureId==HousingSelectedFurniture?"> ":"")}{name}  {(item.Placed?"PLACED":"IN STORAGE")}",42+leftWidth,216+i*38,leftWidth);
            }
            Button("housing/inventory/prev","PREVIOUS ITEMS",42+leftWidth,448,leftWidth*.48f,start>0);
            Button("housing/inventory/next","NEXT ITEMS",50+leftWidth+leftWidth*.48f,448,leftWidth*.48f,start+6<owned.Length);
            Text($"PLACEMENT SLOT {HousingSlot+1}/12  ·  Rotation {HousingPlacementYaw*180/MathF.PI:0}°",tall-161,11,accent);
            Button("housing/slot/prev","PREV SLOT",22,tall-128,115);Button("housing/slot/next","NEXT SLOT",144,tall-128,115);Button("housing/rotate","ROTATE 90°",266,tall-128,115);
            Button("housing/place/"+HousingSelectedFurniture,"PLACE SELECTED ITEM",388,tall-128,wide-410,state.Home is not null&&HousingSelectedFurniture>0);
        }
        else
        {
            Text("Meet other players in their real homes. Visit sets a driving / walking waypoint.",147,12,accent,44);
            var hosts=state.Visitors.Take(8).ToArray();float row=Math.Clamp((tall-308)/8,31,47);
            for(int i=0;i<hosts.Length;i++)
            {
                var host=hosts[i];Button("housing/visit/"+host.HomeId,$"{host.Name}  ·  {(host.AtHome?"AT HOME":"OUT CHASING")}  ·  {(host.VisitsAllowed?"VISIT":"PRIVATE")}",22,203+i*row,wide-44,host.VisitsAllowed);
            }
            if(hosts.Length==0)Text("Connected homeowners will appear here when they join.",216,12,muted);
            if(state.Home is {} h)Button("housing/visits/"+(h.VisitsAllowed?0:1),h.VisitsAllowed?"CLOSE MY HOME TO VISITS":"OPEN MY HOME TO VISITS",22,tall-128,wide-44);
        }
        Text(state?.Message??"",tall-86,10,muted,33);Button("housing/close","RETURN TO CHASE",22,tall-47,wide-44);
        return new(new GuiOverlayPanel{Width=GuiLength.Pixels(width),Height=GuiLength.Pixels(height),Items=[
            new GuiOverlayPanel{Width=GuiLength.Pixels(width),Height=GuiLength.Pixels(height),BackgroundColor=new(3,14,24,185)},
            new GuiOverlayPanel{Translation=new(x,y),Width=GuiLength.Pixels(wide),Height=GuiLength.Pixels(tall),BackgroundColor=new(7,22,31,249),BorderColor=new(49,99,104),BorderThickness=1,CornerRadius=10,Items=elements}]});
    }
}
