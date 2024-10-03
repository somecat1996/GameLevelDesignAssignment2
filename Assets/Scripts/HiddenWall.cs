using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class HiddenWall : MonoBehaviour
{
    private float hiddenTime = 0.3f;

    private float hiddenTimer = 0f;
    private Tilemap tilemap;
    private float alpha = 1f;
    private bool changing = false;
    private bool hidding = false;
    // Start is called before the first frame update
    void Start()
    {
        tilemap = GetComponent<Tilemap>();
    }

    // Update is called once per frame
    void Update()
    {
        if (changing)
        {
            hiddenTimer += Time.deltaTime;
            if (hidding)
            {
                if (hiddenTimer >= hiddenTime)
                {
                    changing = false;
                    tilemap.color = new Color(1, 1, 1, 0);
                }
                else
                {
                    tilemap.color = new Color(1, 1, 1, 1 - hiddenTimer/hiddenTime);
                }
            }
            else
            {
                if (hiddenTimer >= hiddenTime)
                {
                    changing = false;
                    tilemap.color = new Color(1, 1, 1, 1);
                }
                else
                {
                    tilemap.color = new Color(1, 1, 1, hiddenTimer / hiddenTime);
                }
            }
        }
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        Debug.Log(collision.name + "Enter");
        if (collision.name == "body")
        {
            changing = true;
            hidding = true;
            hiddenTimer = 0f;
        }
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        Debug.Log(collision.name + "Exit");
        if (collision.name == "body")
        {
            changing = true;
            hidding = false;
            hiddenTimer = 0f;
        }
    }
}
