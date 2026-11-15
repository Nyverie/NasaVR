using UnityEngine;

public class FondoCamara : MonoBehaviour
{
    [Tooltip("Actívalo para ver el mundo real. Desactívalo para ver un fondo opaco en el Editor.")]
    public bool usarPassthrough = true;

    void Awake()
    {
        var cam = GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = usarPassthrough
            ? new Color(0f, 0f, 0f, 0f)
            : new Color(0.1f, 0.12f, 0.15f, 1f);
    }
}