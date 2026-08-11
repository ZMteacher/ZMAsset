using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using ZM.Asset;

public class APIDemo : MonoBehaviour
{
     
    void Awake()
    {
        ZMAsset.Modules.InitializeAsync(BundleModuleName.Hall);
      
    }

     
}
