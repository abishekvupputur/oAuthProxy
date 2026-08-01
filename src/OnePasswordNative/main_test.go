package main

import (
	"testing"
	"github.com/1password/onepassword-sdk-go"
)

func TestMergeFields_AppendsNewFields(t *testing.T) {
	existing := []onepassword.ItemField{
		{ID: "field1", Title: "Field 1", Value: "old_value"},
	}
	patch := []onepassword.ItemField{
		{ID: "field2", Title: "Field 2", Value: "new_value"},
	}

	merged := mergeFields(existing, patch)

	if len(merged) != 2 {
		t.Fatalf("Expected 2 fields, got %d", len(merged))
	}
	if merged[1].ID != "field2" || merged[1].Value != "new_value" {
		t.Errorf("Expected field2 to be appended with new_value, got %v", merged[1])
	}
}

func TestMergeFields_MatchesByIDAndUpdates(t *testing.T) {
	existing := []onepassword.ItemField{
		{ID: "field1", Title: "Field 1", Value: "old_value"},
	}
	patch := []onepassword.ItemField{
		{ID: "field1", Title: "Field 1 Updated", Value: "new_value"},
	}

	merged := mergeFields(existing, patch)

	if len(merged) != 1 {
		t.Fatalf("Expected 1 field, got %d", len(merged))
	}
	if merged[0].Value != "new_value" {
		t.Errorf("Expected field1 value to be updated to new_value, got %s", merged[0].Value)
	}
}

func TestMergeFields_MatchesByTitleAndUpdates(t *testing.T) {
	existing := []onepassword.ItemField{
		{ID: "random_id", Title: "My Field", Value: "old_value"},
	}
	patch := []onepassword.ItemField{
		{ID: "my_field", Title: "My Field", Value: "new_value"},
	}

	merged := mergeFields(existing, patch)

	if len(merged) != 1 {
		t.Fatalf("Expected 1 field, got %d", len(merged))
	}
	if merged[0].Value != "new_value" {
		t.Errorf("Expected My Field value to be updated to new_value, got %s", merged[0].Value)
	}
	if merged[0].ID != "random_id" {
		t.Errorf("Expected ID to remain random_id, got %s", merged[0].ID)
	}
}

func TestMergeFields_EmptyExisting(t *testing.T) {
	var existing []onepassword.ItemField
	patch := []onepassword.ItemField{
		{ID: "field1", Title: "Field 1", Value: "value1"},
	}

	merged := mergeFields(existing, patch)

	if len(merged) != 1 {
		t.Fatalf("Expected 1 field, got %d", len(merged))
	}
}
