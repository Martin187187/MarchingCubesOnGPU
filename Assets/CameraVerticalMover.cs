using UnityEngine;

public class CameraVerticalMover : MonoBehaviour
{
    public Transform cameraTransform; // Assign the camera in Inspector (if not directly a child)
    public float tiltSpeed = 2f; // How fast the camera tilts
    public float minTiltAngle = -30f; // Lower limit (look down)
    public float maxTiltAngle = 60f; // Upper limit (look up)
    
    private float tiltAngle = 0f; // Current tilt angle

    void Update()
    {
        // Get vertical input (Mouse or Controller)
        float tiltInput = Input.GetAxis("Mouse Y"); // Inverted by default

        // Update tilt angle
        tiltAngle -= tiltInput * tiltSpeed;
        tiltAngle = Mathf.Clamp(tiltAngle, minTiltAngle, maxTiltAngle); // Keep within bounds

        // Apply rotation only to X-axis (tilting)
        cameraTransform.localRotation = Quaternion.Euler(tiltAngle, 0f, 0f);
    }
}