﻿namespace MPowerKit.VirtualizeListView;

public static class BuilderExtensions
{
    public static MauiAppBuilder UseMPowerKitListView(this MauiAppBuilder builder)
    {
        builder.ConfigureMauiHandlers(handlers =>
        {
#if ANDROID
            handlers.AddHandler<VirtualizeListView, VirtualizeListViewHandler>();
#endif

#if ANDROID || MACIOS
            handlers.AddHandler<FixedRefreshView, FixedRefreshViewHandler>();
#endif
        });

        return builder;
    }
}