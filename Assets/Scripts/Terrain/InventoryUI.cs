using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class InventoryUI : MonoBehaviour
{
    public TMP_Text inventoryText;
    public TerrainController terrainManager;

    void Update()
    {
        UpdateInventoryUI();
    }

    void UpdateInventoryUI()
    {
        if (terrainManager == null || inventoryText == null) return;

        string displayText = "Inventory:\n";
        foreach (var item in terrainManager.inventory)
        {
            displayText += $"{item.Key}: {item.Value}\n";
        }

        inventoryText.text = displayText;
    }
}