// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using Grpc.Core;
using OpenTelemetry.Trace;
using cartservice.cartstore;
using OpenFeature;
using Oteldemo;
using Microsoft.AspNetCore.Http;
using System.Net.Http;


namespace cartservice.services;

public class CartService : Oteldemo.CartService.CartServiceBase
{
    private static readonly Empty Empty = new();
    private readonly Random random = new Random();
    private readonly ICartStore _badCartStore;
    private readonly ICartStore _cartStore;
    private readonly IFeatureClient _featureFlagHelper;

    public CartService(ICartStore cartStore, ICartStore badCartStore, IFeatureClient featureFlagService)
    {
        _badCartStore = badCartStore;
        _cartStore = cartStore;
        _featureFlagHelper = featureFlagService;
    }

    // Server-side: Extract X-Service-Name from incoming request and add to span
    public void AddServiceNameToSpan(HttpContext context)
    {
        var serviceName = context.Request.Headers["X-Service-Name"].ToString();
        if (!string.IsNullOrEmpty(serviceName))
        {
            var activity = Activity.Current;
            activity?.SetTag("net.peer.name", serviceName);

            Console.WriteLine($"Service name: {serviceName} added to span as net.peer.name");
        }
    }


    public override async Task<Empty> AddItem(AddItemRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("app.user.id", request.UserId);
        activity?.SetTag("app.product.id", request.Item.ProductId);
        activity?.SetTag("app.product.quantity", request.Item.Quantity);

        AddServiceNameToSpan(context.GetHttpContext());

        await _cartStore.AddItemAsync(request.UserId, request.Item.ProductId, request.Item.Quantity);
        return Empty;
    }

    public override async Task<Cart> GetCart(GetCartRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        AddServiceNameToSpan(context.GetHttpContext());

        activity?.SetTag("app.user.id", request.UserId);
        activity?.AddEvent(new("Fetch cart"));

        var cart = await _cartStore.GetCartAsync(request.UserId);
        var totalCart = 0;
        foreach (var item in cart.Items)
        {
            totalCart += item.Quantity;
        }
        activity?.SetTag("app.cart.items.count", totalCart);

        return cart;
    }

    public override async Task<Empty> EmptyCart(EmptyCartRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        AddServiceNameToSpan(context.GetHttpContext());

        activity?.SetTag("app.user.id", request.UserId);
        activity?.AddEvent(new("Empty cart"));

        try
        {
            // Throw 1/10 of the time to simulate a failure when the feature flag is enabled
            if (await _featureFlagHelper.GetBooleanValue("cartServiceFailure", false) && random.Next(10) == 0)
            {
                await _badCartStore.EmptyCartAsync(request.UserId);
            }
            else
            {
                await _cartStore.EmptyCartAsync(request.UserId);
            }
        }
        catch (RpcException ex)
        {
            Activity.Current?.RecordException(ex);
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }

        return Empty;
    }
}
