using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class HoverUi : MonoBehaviour
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


        inventoryText.text = terrainManager.terrainType.ToString();
    }
}