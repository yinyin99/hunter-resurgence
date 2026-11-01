// The HTML of the sign-in: the launcher's start page and the small pages around Steam's. Plain HTML and ES5 (the old
// launcher's browser is Chromium 25), English only, no external files. Values are HTML-encoded; the script has none.
using System.Net;

namespace Resurgence.Server;

public static class AuthPages
{
    static string Page(string body, string script = "") => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>Sign in</title>
        <style>body{font-family:Arial,sans-serif;background:#222;color:#eee;text-align:center;padding-top:60px}
        button{font-size:20px;padding:12px 32px;cursor:pointer}input{font-size:18px;padding:6px}
        a{color:#f90}.small{font-size:13px;color:#aaa}.err{color:#f66}</style></head>
        <body>{{body}}<p id="msg" class="err"></p>{{(script == "" ? "" : "<script>" + script + "</script>")}}</body></html>
        """;

    static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>The page the launcher shows. Signed out: sign in with Steam in the player's own browser (the launcher's
    /// cannot load Steam's pages) or, as a second link, in this window. Signed in: Play, which asks the server for a
    /// launch URL and hands it to the launcher.</summary>
    public static string Start(string? handle) => Page(handle is null
        ? """
          <h1>Welcome</h1><p>Sign in with Steam to play on this server.</p>
          <p><button id="steam" type="button">Sign in with Steam</button></p>
          <p id="code"></p>
          <p class="small"><a href="/auth/steam/login?next=%2F">Sign in inside this window instead</a></p>
          """
        : $"""
          <h1>Hello, {E(handle)}</h1><p><button id="play" type="button">Play</button></p>
          <p class="small"><a href="#" id="out">Not you? Sign out</a></p>
          """, """
        function $(id){return document.getElementById(id)}
        function say(t){$('msg').innerHTML='';$('msg').appendChild(document.createTextNode(t))}
        function call(method,url,cb){var x=new XMLHttpRequest();x.open(method,url,true);
          x.setRequestHeader('X-Requested-With','XMLHttpRequest');
          x.onreadystatechange=function(){if(x.readyState==4){var j=null;try{j=JSON.parse(x.responseText)}catch(e){}cb(x.status,j)}};
          x.send(method=='POST'?'':null)}
        function poll(code){var t=setInterval(function(){call('GET','/auth/link/'+code,function(s,j){
          if(s==200&&j&&j.status=='ok'){clearInterval(t);location.reload()}
          else if(s!=200){clearInterval(t);say('The sign-in ran out of time. Try again.')}})},2000)}
        if($('steam'))$('steam').onclick=function(){call('POST','/auth/link',function(s,j){
          if(s!=200||!j){say('Could not start the sign-in.');return}
          $('code').innerHTML='';$('code').appendChild(document.createTextNode('Your browser opens. Confirm the code '+j.userCode+' there.'));
          if(window.ewlauncher&&ewlauncher.openURL)ewlauncher.openURL(j.url);else window.open(j.url);
          poll(j.code)})};
        if($('play'))$('play').onclick=function(){call('POST','/auth/launch',function(s,j){
          if(s==200&&j&&j.url){if(window.ewlauncher)ewlauncher.startGame(j.url);else say('Open this page in the launcher to play.')}
          else{say('Could not start the game. Sign in again.');if(s==401)location.reload()}})};
        if($('out'))$('out').onclick=function(){call('POST','/auth/signout',function(){location.reload()});return false};
        """);

    /// <summary>The first sign-in: Steam gives no name, so the player picks one.</summary>
    public static string Handle(string csrf, string handle, string? error) => Page($"""
        <h1>Choose your name</h1><p>This is the name other hunters see.</p>
        <form method="post" action="/auth/handle"><input type="hidden" name="csrf" value="{E(csrf)}">
        <p><input name="handle" value="{E(handle)}" maxlength="20" autocomplete="off" autofocus></p>
        <p class="small">3 to 20 letters, digits, - or _.</p>
        {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
        <p><button type="submit">Continue</button></p></form>
        """);

    /// <summary>A launcher is waiting for this sign-in: confirm only when the code is the one it shows.</summary>
    public static string Confirm(string csrf, string handle, string userCode) => Page($"""
        <h1>Sign in the launcher?</h1>
        <p>You are <b>{E(handle)}</b>. The launcher that shows the code <b>{E(userCode)}</b> will be signed in as you.</p>
        <p class="small">Confirm only if this code is on your launcher right now.</p>
        <form method="post" action="/auth/confirm"><input type="hidden" name="csrf" value="{E(csrf)}">
        <p><button type="submit">Yes, sign in</button></p></form>
        """);

    public static string Done() => Page("<h1>Done</h1><p>Go back to the launcher.</p>");

    public static string Problem(string text) => Page($"<h1>Sorry</h1><p>{E(text)}</p>");
}
