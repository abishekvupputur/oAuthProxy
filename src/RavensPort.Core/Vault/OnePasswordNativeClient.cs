using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public static class OnePasswordNativeClient
{
    private const string DllName = "onepassword.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr InitializeOP([MarshalAs(UnmanagedType.LPUTF8Str)] string accountName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VaultList();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VaultCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemList([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemGet([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemEdit([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemDelete([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeString(IntPtr ptr);

    private static string? GetStringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var str = Marshal.PtrToStringUTF8(ptr);
        FreeString(ptr);
        return str;
    }

    public static void Initialize(string accountName)
    {
        var errPtr = InitializeOP(accountName ?? "");
        var err = GetStringAndFree(errPtr);
        if (!string.IsNullOrEmpty(err))
        {
            throw new InvalidOperationException($"Failed to initialize 1Password SDK: {err}");
        }
    }

    private static JsonNode? ParseResponse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj && obj.ContainsKey("error"))
        {
            throw new VaultCliException(obj["error"]!.GetValue<string>());
        }
        return node;
    }

    public static JsonArray? ListVaults()
    {
        var json = GetStringAndFree(VaultList());
        return ParseResponse(json) as JsonArray;
    }

    public static JsonNode? CreateVault(string name, string description)
    {
        var json = GetStringAndFree(VaultCreate(name, description));
        return ParseResponse(json);
    }

    public static JsonArray? ListItems(string vaultId)
    {
        var json = GetStringAndFree(ItemList(vaultId));
        return ParseResponse(json) as JsonArray;
    }

    public static JsonNode? GetItem(string vaultId, string itemId)
    {
        var json = GetStringAndFree(ItemGet(vaultId, itemId));
        return ParseResponse(json);
    }

    public static JsonNode? CreateItem(string vaultId, string itemJson)
    {
        var json = GetStringAndFree(ItemCreate(vaultId, itemJson));
        return ParseResponse(json);
    }

    public static JsonNode? EditItem(string vaultId, string itemId, string itemJson)
    {
        var json = GetStringAndFree(ItemEdit(vaultId, itemId, itemJson));
        return ParseResponse(json);
    }

    public static void DeleteItem(string vaultId, string itemId)
    {
        var err = GetStringAndFree(ItemDelete(vaultId, itemId));
        if (!string.IsNullOrEmpty(err))
        {
            throw new VaultCliException(err);
        }
    }
}
