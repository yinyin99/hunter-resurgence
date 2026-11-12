// The launcher's start page: the one sign-in page the server still draws. The old launcher's browser is Chromium 25 (no grid, no CSS variables, no flex gap, ES5), English only, no external files.
// Its colours and type are the website's (src/Ui/wwwroot/theme.css) as literals. The pages in the player's own browser are the website's (docs/website.md#sign-in).
using System.Net;

namespace Resurgence.Server;

public static class AuthPages
{
    static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>The line to the operator's Discord; nothing without the link.</summary>
    static string Discord(string? url) => url is null ? "" : $"""<p><a href="{E(url)}" target="_blank" rel="noopener">Join the Discord: news, help and the operator</a></p>""";

    const string Style = """
        html,body{margin:0;background:#000}
        body{padding:24px 16px;color:#bbb;font:400 14px/20px Roboto,Arial,sans-serif;text-align:center}
        .card{max-width:400px;margin:0 auto;padding:32px 24px;background:#161616;border-radius:8px;-webkit-box-sizing:border-box;box-sizing:border-box}
        h1{margin:0 0 16px;font:400 31px/38px "Helvetica Neue",Helvetica,Arial,sans-serif;color:#eee;word-wrap:break-word}
        p{margin:0 0 24px}
        a{color:#f9560d;text-decoration:none}
        a:hover{color:#fb8a58}
        .btn{display:inline-block;max-width:100%;padding:11px 19px;border:0;border-radius:4px;background:#f9370d;color:#fff;font:400 17.5px/20px Roboto,Arial,sans-serif;cursor:pointer}
        .btn:hover{background:#e72d06}
        .btn.alt{background:#444}
        .btn.alt:hover{background:#555}
        .btn:focus,a:focus{outline:2px solid #f9560d;outline-offset:2px}
        .more{margin:40px 0 0}
        .more p:last-child,.card>p:last-child{margin-bottom:0}
        #code:empty,#msg:empty{display:none}
        #msg{color:#f66}
        """;

    const string Script = """
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
        $('new').onclick=function(){if(window.ewlauncher&&ewlauncher.openURL){ewlauncher.openURL('hunter-resurgence://open');ewlauncher.exitLauncher()}
          else say('Open this page in the original launcher to open the new one.')};
        if($('out'))$('out').onclick=function(){call('POST','/auth/signout',function(){location.reload()});return false};
        """;

    /// <summary>The page the launcher shows (both states also link the launcher that replaces it). Signed out: sign in with Steam in the player's own browser (the launcher's
    /// cannot load Steam's pages). Signed in: Play, which asks the server for a launch URL and hands it to the launcher.</summary>
    public static string Start(string? handle, string? discord = null) => $"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Hunter Resurgence</title>
        <style>
        {Style}</style></head>
        <body><div class="card">
        {(handle is null
            ? """
              <h1>Welcome</h1><p>Sign in with Steam to play on this server.</p>
              <p><button id="steam" class="btn" type="button">Sign in with Steam</button></p>
              """
            : $"""
              <h1>Hello, {E(handle)}</h1><p><button id="play" class="btn" type="button">Play</button></p>
              <p><a href="#" id="out">Not you? Sign out</a></p>
              {Discord(discord)}
              """)}
        <p id="code"></p><p id="msg"></p>
        <div class="more"><p><button id="new" class="btn alt" type="button">Open the new launcher</button></p>
        <p><a href="https://github.com/yinyin99/hunter-resurgence/releases/latest">Download it</a></p></div>
        </div><script>
        {Script}</script></body></html>
        """;
}
