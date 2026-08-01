package main

/*
#include <stdlib.h>
*/
import "C"
import (
	"context"
	"encoding/json"
	"unsafe"

	"github.com/1password/onepassword-sdk-go"
)

var opClient *onepassword.Client

//export InitializeOP
func InitializeOP(accountName *C.char) *C.char {
	accName := C.GoString(accountName)
	
	opts := []onepassword.ClientOption{
		onepassword.WithIntegrationInfo("RavensPort", "1.0.0"),
		onepassword.WithDesktopAppIntegration(accName),
	}

	var err error
	opClient, err = onepassword.NewClient(context.Background(), opts...)
	if err != nil {
		return C.CString(err.Error())
	}
	return nil
}

//export VaultList
func VaultList() *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	vaults, err := opClient.Vaults().List(context.Background())
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(vaults)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export VaultCreate
func VaultCreate(name *C.char, description *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	descStr := C.GoString(description)
	params := onepassword.VaultCreateParams{
		Title:       C.GoString(name),
		Description: &descStr,
	}
	vault, err := opClient.Vaults().Create(context.Background(), params)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(vault)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export ItemList
func ItemList(vaultID *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	items, err := opClient.Items().List(context.Background(), C.GoString(vaultID))
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(items)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export ItemGet
func ItemGet(vaultID *C.char, itemID *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	item, err := opClient.Items().Get(context.Background(), C.GoString(vaultID), C.GoString(itemID))
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(item)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export ItemCreate
func ItemCreate(vaultID *C.char, itemJson *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	var params onepassword.ItemCreateParams
	err := json.Unmarshal([]byte(C.GoString(itemJson)), &params)
	if err != nil {
		return C.CString(`{"error": "invalid json: ` + err.Error() + `"}`)
	}
	params.VaultID = C.GoString(vaultID)
	
	item, err := opClient.Items().Create(context.Background(), params)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(item)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export ItemEdit
func ItemEdit(vaultID *C.char, itemID *C.char, itemJson *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	ctx := context.Background()
	vid := C.GoString(vaultID)
	iid := C.GoString(itemID)
	
	existingItem, err := opClient.Items().Get(ctx, vid, iid)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	
	var patch onepassword.Item
	err = json.Unmarshal([]byte(C.GoString(itemJson)), &patch)
	if err != nil {
		return C.CString(`{"error": "invalid json: ` + err.Error() + `"}`)
	}
	
	if patch.Title != "" {
		existingItem.Title = patch.Title
	}
	if patch.Category != "" {
		existingItem.Category = patch.Category
	}
	if patch.Notes != "" {
		existingItem.Notes = patch.Notes
	}
	
	if patch.Fields != nil {
		for _, pf := range patch.Fields {
			found := false
			for i, ef := range existingItem.Fields {
				if ef.ID == pf.ID || ef.Title == pf.Title {
					existingItem.Fields[i].Value = pf.Value
					found = true
					break
				}
			}
			if !found {
				existingItem.Fields = append(existingItem.Fields, pf)
			}
		}
	}
	
	updatedItem, err := opClient.Items().Put(ctx, existingItem)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	bytes, err := json.Marshal(updatedItem)
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return C.CString(string(bytes))
}

//export ItemDelete
func ItemDelete(vaultID *C.char, itemID *C.char) *C.char {
	if opClient == nil {
		return C.CString(`{"error": "client not initialized"}`)
	}
	err := opClient.Items().Delete(context.Background(), C.GoString(vaultID), C.GoString(itemID))
	if err != nil {
		return C.CString(`{"error": "` + err.Error() + `"}`)
	}
	return nil
}

//export FreeString
func FreeString(ptr *C.char) {
	if ptr != nil {
		C.free(unsafe.Pointer(ptr))
	}
}

func main() {}
