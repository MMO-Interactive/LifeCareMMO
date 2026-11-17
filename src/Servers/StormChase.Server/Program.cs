using StormChase.Server;

try
{
    var options=ServerOptions.Parse(args);
    if(options.SelfTest) return await ServerSelfTests.Run(options);
    if(options.NativeSelfTest) return await ServerNativeTests.Run(options);
    if(options.MasterSelfTest) return await MasterSelfTests.Run(options);
    await using var app=ServerApi.Create(options);
    await app.RunAsync();
    return 0;
}
catch(Exception error)
{
    Console.Error.WriteLine("Storm Chase server stopped: "+error.Message);
    return 1;
}
